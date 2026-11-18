using System.Diagnostics;
using PcHub.Services;

namespace PcHub.ViewModels;

public enum ToolState
{
    Stopped,
    Starting,
    Running,
    Ready,
    Stopping,
}

public sealed class ToolViewModel : ObservableObject
{
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(90);
    private readonly ActivityLog _log;

    private ToolState _state;
    private string _stateText = "Stopped";
    private string _vramText = "—";
    private string _gpuText = "—";
    private string _uptimeText = "";
    private string? _error;

    private bool _wasRunning;
    private bool _firstUpdate = true;
    private volatile bool _stopRequested;
    private bool _stopping;
    private bool _slowStartWarned;
    private DateTime? _launchedAt;
    private long _runStart;
    private long _peakVram;

    public ToolViewModel(ToolDefinition def, ActivityLog log)
    {
        Definition = def;
        _log = log;
    }

    public ToolDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Definition.Url);
    public bool HasLog => Definition.Hidden;
    public bool IsOllama => Definition.IsOllama;
    public string Location => Definition.WorkingDirectory ?? Definition.FileName;
    public string Subtitle => Definition.IsOllama ? "LLM server" : Definition.Hidden ? "Background service" : "App";

    public IReadOnlyList<ProcInfo> Processes { get; private set; } = [];
    public long VramBytes { get; private set; }

    public ToolState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(CanStart));
                Raise(nameof(CanStop));
                Raise(nameof(IsUp));
                Raise(nameof(IsTransitioning));
            }
        }
    }

    /// <summary>Running or ready (green dot).</summary>
    public bool IsUp => State is ToolState.Running or ToolState.Ready;

    /// <summary>Starting or stopping (amber dot).</summary>
    public bool IsTransitioning => State is ToolState.Starting or ToolState.Stopping;

    public bool CanStart => State == ToolState.Stopped;
    public bool CanStop => State is ToolState.Running or ToolState.Ready || (State == ToolState.Starting && Processes.Count > 0);

    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string VramText { get => _vramText; private set => Set(ref _vramText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }

    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public void MarkLaunched(Process? launched)
    {
        Error = null;
        _launchedAt = DateTime.UtcNow;
        _slowStartWarned = false;
        _stopRequested = false;
        State = ToolState.Starting;
        StateText = "Starting…";

        if (launched is null) return;
        try
        {
            launched.EnableRaisingEvents = true;
            launched.Exited += (_, _) => OnLaunchedExited(launched);
        }
        catch (InvalidOperationException)
        {
            // Exited before we could watch it; the next scan will show it as stopped.
        }
    }

    public void BeginStop()
    {
        _stopRequested = true;
        _stopping = true;
        Error = null;
        State = ToolState.Stopping;
        StateText = "Stopping…";
    }

    public void EndStop() => _stopping = false;

    private void OnLaunchedExited(Process p)
    {
        int code;
        try { code = p.ExitCode; }
        catch (InvalidOperationException) { return; }
        finally { p.Dispose(); }

        // Wrapper launchers (wscript, cmd for windowed apps) exit right after starting the real app.
        // On macOS an app bundle is started by "open -a", which fails with a non-zero code if the app won't open.
        bool viaOpen = Definition.FileName.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
        if (_stopRequested || code == 0 || (!Definition.Hidden && !viaOpen)) return;
        string msg = viaOpen
            ? $"macOS couldn't open {Name} (code {code}). Was it moved, or blocked in Privacy & Security?"
            : $"Exited unexpectedly (code {code}). Open its log for details.";
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Error = msg;
            _log.Error(Name, msg);
        });
    }

    /// <summary>Applies one scan. Runs on the UI thread.</summary>
    public void Update(ToolStatus status)
    {
        Processes = status.Processes;
        VramBytes = status.VramBytes;
        bool running = status.Processes.Count > 0;
        var now = DateTime.UtcNow;

        if (running && !_wasRunning)
        {
            _runStart = status.EarliestStart;
            _peakVram = 0;
            if (_launchedAt is not null)
            {
                _log.Info(Name, "Started.");
            }
            else if (_firstUpdate)
            {
                _log.Info(Name, "Already running.");
            }
            else
            {
                _log.Info(Name, "Opened outside PCHUB.");
            }
        }

        if (running)
        {
            _peakVram = Math.Max(_peakVram, status.VramBytes);
        }

        if (!running && _wasRunning)
        {
            string ran = _runStart > 0 ? Format.Duration(now - DateTime.FromFileTimeUtc(_runStart)) : "?";
            string summary = $"ran {ran}, peak VRAM {Format.Bytes(_peakVram)}";
            if (_stopRequested) _log.Info(Name, $"Stopped ({summary}).");
            else _log.Info(Name, $"Closed ({summary}).");
            _launchedAt = null;
        }

        _wasRunning = running;
        _firstUpdate = false;

        if (_stopping)
        {
            // Stay "Stopping…" until the controller finishes.
        }
        else if (running)
        {
            if (Definition.Port is > 0 && status.PortOpen == false)
            {
                State = ToolState.Starting;
                StateText = "Loading…";
            }
            else
            {
                State = Definition.Port is > 0 ? ToolState.Ready : ToolState.Running;
                StateText = Definition.Port is > 0 ? "Ready" : "Running";
                _launchedAt = null;
            }
        }
        else if (_launchedAt is not null && now - _launchedAt < StartGrace)
        {
            State = ToolState.Starting;
            StateText = "Starting…";
        }
        else
        {
            if (_launchedAt is not null && !_slowStartWarned)
            {
                _slowStartWarned = true;
                string hint = Definition.Hidden ? " Check its log." : "";
                Error = "Didn't start within 90 seconds." + hint;
                _log.Warn(Name, Error);
            }

            _launchedAt = null;
            State = ToolState.Stopped;
            StateText = "Stopped";
        }

        VramText = running ? Format.Bytes(status.VramBytes) : "—";
        GpuText = running ? $"{status.GpuPercent:0}%" : "—";
        UptimeText = running && _runStart > 0 ? Format.Duration(now - DateTime.FromFileTimeUtc(_runStart)) : "";
    }
}
