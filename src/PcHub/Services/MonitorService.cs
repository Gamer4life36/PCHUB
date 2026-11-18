using System.Diagnostics;
using PcHub.Platform;

namespace PcHub.Services;

public sealed record ToolStatus(
    string ToolId,
    IReadOnlyList<ProcInfo> Processes,
    long VramBytes,
    double GpuPercent,
    long EarliestStart,
    bool? PortOpen);

public sealed record GpuProcRow(ProcInfo Proc, GpuProcUsage Usage, string? OwnerToolId);

public sealed record ProcessPart(
    IReadOnlyDictionary<string, ToolStatus> Tools,
    IReadOnlyList<GpuProcRow> GpuProcesses,
    double ScanMs,
    string? CounterError);

public sealed record MonitorSnapshot(GpuSample? Gpu, ProcessPart? Processes);

/// <summary>
/// Background sampler. GPU totals every tick (NVML costs microseconds); processes, per-process
/// VRAM and ports every second tick. Results are immutable snapshots handed to the UI.
/// Low-power mode doubles the tick and halves the scan rate for older machines.
/// </summary>
public sealed class MonitorService : IDisposable
{
    private readonly IGpuMonitor _gpu;
    private readonly IPlatform _os = Os.Current;
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<ToolDefinition> _tools = [];
    private volatile bool _scanNow;
    private volatile bool _lowPower;
    private GpuSample? _lastCounterSample;
    private Task? _loop;

    public MonitorService(IGpuMonitor gpu, bool lowPower)
    {
        _gpu = gpu;
        _lowPower = lowPower;
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action<MonitorSnapshot>? SnapshotReady;
    public event Action<Exception>? Faulted;

    public bool LowPower
    {
        get => _lowPower;
        set => _lowPower = value;
    }

    private volatile bool _background;

    /// <summary>True while the window is minimized: sample every 3 s and scan every 9 s.</summary>
    public bool Background
    {
        get => _background;
        set => _background = value;
    }

    /// <summary>
    /// Processes PCHUB started for a tool: pid → (tool id, create time, filled in on first sight). They and
    /// their children belong to that tool even when their exe lives outside its folder (on macOS a venv's
    /// python is a symlink to Homebrew's, and proc_pidpath reports the real file).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, LaunchRoot> _roots = new();

    private sealed class LaunchRoot(string toolId)
    {
        public string ToolId { get; } = toolId;
        public long CreateTime { get; set; }
    }

    public void RegisterLaunch(string toolId, Process? launched)
    {
        if (launched is null) return;
        try
        {
            if (!launched.HasExited) _roots[launched.Id] = new LaunchRoot(toolId);
        }
        catch (InvalidOperationException)
        {
            // Already gone (e.g. "open -a" handing off to Launch Services); path matching still applies.
        }
    }

    public void SetTools(IReadOnlyList<ToolDefinition> tools)
    {
        _tools = tools;
        _scanNow = true;
    }

    /// <summary>Do a full process scan on the next tick instead of waiting.</summary>
    public void RequestScan() => _scanNow = true;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Total VRAM in use right now (used to measure before/after freeing).</summary>
    public ulong? ReadUsedBytesNow() => _gpu.UsedBytesNow();

    private async Task RunAsync(CancellationToken ct)
    {
        long tick = 0;
        int consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            bool low = _lowPower;
            bool bg = _background;
            try
            {
                GpuSample? gpu = _gpu.ReadFast();

                ProcessPart? part = null;
                int scanEvery = bg || low ? 3 : 2;
                if (tick % scanEvery == 0 || _scanNow)
                {
                    _scanNow = false;
                    part = Scan();
                }

                gpu ??= _lastCounterSample;
                SnapshotReady?.Invoke(new MonitorSnapshot(gpu, part));
                consecutiveErrors = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep sampling; report the first failure of a streak so it is never silent.
                if (consecutiveErrors++ == 0)
                {
                    Faulted?.Invoke(ex);
                }
            }

            tick++;
            try
            {
                await Task.Delay(bg ? 3000 : low ? 2000 : 1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ProcessPart Scan()
    {
        long t0 = Stopwatch.GetTimestamp();
        var tools = _tools;
        var procs = _os.ScanProcesses();
        var reading = _gpu.ReadScan(procs);
        var usage = reading.PerProcess;
        var owner = Attribute(procs, tools);
        if (reading.Sample is not null) _lastCounterSample = reading.Sample;

        // Read the OS table of listening ports once (instant; no connection attempts).
        var listening = PortProbe.ListeningPorts();
        var ports = tools.Where(t => t.Port is > 0).ToDictionary(t => t.Id, t => listening.Contains(t.Port!.Value));

        var byTool = new Dictionary<string, List<ProcInfo>>(tools.Count);
        foreach (var (pid, id) in owner)
        {
            if (!procs.TryGetValue(pid, out var p)) continue;
            if (!byTool.TryGetValue(id, out var l)) byTool[id] = l = new List<ProcInfo>();
            l.Add(p);
        }

        var statuses = new Dictionary<string, ToolStatus>(tools.Count);
        foreach (var t in tools)
        {
            var mine = byTool.GetValueOrDefault(t.Id) ?? [];
            long vram = 0;
            double gpuPct = 0;
            long earliest = long.MaxValue;
            foreach (var p in mine)
            {
                if (usage.TryGetValue(p.Pid, out var u))
                {
                    vram += Math.Max(0, u.DedicatedBytes);
                    gpuPct += u.UtilPercent;
                }

                if (p.CreateTime > 0 && p.CreateTime < earliest) earliest = p.CreateTime;
            }

            bool? portOpen = ports.TryGetValue(t.Id, out bool o) ? o : null;
            statuses[t.Id] = new ToolStatus(t.Id, mine, vram, Math.Min(100, gpuPct), earliest == long.MaxValue ? 0 : earliest, portOpen);
        }

        var rows = new List<GpuProcRow>();
        foreach (var (pid, u) in usage)
        {
            if (u.DedicatedBytes >= 0 && u.DedicatedBytes < 16L * 1024 * 1024 && u.UtilPercent < 0.5) continue;
            if (!procs.TryGetValue(pid, out var p)) continue;
            rows.Add(new GpuProcRow(p, u, owner.GetValueOrDefault(pid)));
        }

        rows.Sort((a, b) => b.Usage.DedicatedBytes.CompareTo(a.Usage.DedicatedBytes));
        return new ProcessPart(statuses, rows, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, reading.Error);
    }

    /// <summary>
    /// pid to tool id. A process belongs to a tool when its exe is one of the tool's exact files or sits
    /// inside one of its folders (the most specific match wins, so nested installs don't steal each
    /// other's processes). A child also belongs to its parent's tool (venv launchers start the real
    /// python.exe from elsewhere), except browsers, shells and Windows programs the tool merely opened.
    /// A parent must be older than its child, so a reused PID can't pull in a stranger.
    /// </summary>
    private Dictionary<int, string> Attribute(IReadOnlyDictionary<int, ProcInfo> procs, IReadOnlyList<ToolDefinition> tools)
    {
        var owner = new Dictionary<int, string>();
        if (tools.Count == 0) return owner;

        foreach (var p in procs.Values)
        {
            if (p.Path is null) continue;
            string? best = null;
            int bestScore = 0;
            foreach (var t in tools)
            {
                int score = MatchScore(t, p.Path);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = t.Id;
                }
            }

            if (best is not null) owner[p.Pid] = best;
        }

        // Processes PCHUB launched. The create time is pinned on first sight so a reused PID never counts.
        foreach (var (pid, root) in _roots)
        {
            if (!procs.TryGetValue(pid, out var p) || tools.All(t => t.Id != root.ToolId))
            {
                _roots.TryRemove(pid, out _);
                continue;
            }

            if (root.CreateTime == 0) root.CreateTime = p.CreateTime;
            if (root.CreateTime != p.CreateTime)
            {
                _roots.TryRemove(pid, out _);
                continue;
            }

            owner.TryAdd(pid, root.ToolId);
        }

        foreach (var p in procs.Values)
        {
            if (owner.ContainsKey(p.Pid) || !PathSafety.CanInherit(p)) continue;

            var child = p;
            for (int depth = 0; depth < 12; depth++)
            {
                if (!procs.TryGetValue(child.ParentPid, out var parent) || parent.Pid == child.Pid) break;
                if (parent.CreateTime == 0 || child.CreateTime == 0 || parent.CreateTime > child.CreateTime) break;
                if (owner.TryGetValue(parent.Pid, out string? id))
                {
                    owner[p.Pid] = id;
                    break;
                }

                if (!PathSafety.CanInherit(parent)) break; // e.g. a browser's helpers stay the browser's
                child = parent;
            }
        }

        return owner;
    }

    /// <summary>0 = not this tool's; exact file beats folder; a longer (more specific) folder beats a shorter one.</summary>
    public static int MatchScore(ToolDefinition t, string path)
    {
        foreach (string f in t.MatchFiles)
        {
            if (path.Equals(f, Os.Current.PathComparison)) return int.MaxValue;
        }

        int best = 0;
        foreach (string f in t.MatchPaths)
        {
            if (f.Length > best && PathSafety.IsUnder(path, f)) best = f.Length;
        }

        return best;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { /* ended by cancellation */ }
        _cts.Dispose();
    }
}
