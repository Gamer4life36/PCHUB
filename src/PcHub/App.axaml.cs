using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PcHub.Platform;
using PcHub.Services;
using PcHub.ViewModels;

namespace PcHub;

public partial class App : Application
{
    /// <summary>A web address passed on the command line ("--open url"), shown once the app list is loaded.</summary>
    public static string? StartupUrl { get; set; }

    private IGpuMonitor? _gpu;
    private MonitorService? _monitor;
    private ActivityLog? _log;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private AppSettings _settings = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<string> _pendingUrls = new();
    private bool _ready;
    private string? _lastUiError;
    private DateTime _lastUiErrorAt;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write("Unhandled (fatal)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            CrashLog.Write("Unobserved task", a.Exception);
            a.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += OnUiException;

        _settings = ToolStore.LoadSettings();
        _gpu = CreateGpuSafely();
        _monitor = new MonitorService(_gpu, _settings.LowPower);
        _log = new ActivityLog(Dispatcher.UIThread);
        _vm = new MainViewModel(_gpu, _monitor, _log, _settings, Dispatcher.UIThread);

        _window = new MainWindow(_settings) { DataContext = _vm };
        desktop.MainWindow = _window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.Exit += (_, _) => Shutdown();

        // Sample gently while minimized: nobody is looking, and a laptop GPU can go back to sleep.
        _window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) _monitor.Background = _window.WindowState == WindowState.Minimized;
        };

        SingleInstance.Attach(msg => Dispatcher.UIThread.Post(() => OnMessage(msg)));
        _ = StartAsync();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A broken driver or slow-waking laptop GPU must not stop PCHUB from opening.</summary>
    private static IGpuMonitor CreateGpuSafely()
    {
        try
        {
            return Os.Current.CreateGpuMonitor(ToolStore.LoadSettings());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("GPU monitor failed to start", ex);
            return new NoGpuMonitor("GPU detection failed (" + ex.GetBaseException().Message + "). Process tracking still works.");
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _vm!.InitializeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Keep the window usable and say so, instead of silently dropping queued requests.
            CrashLog.Write("Startup", ex);
            _log?.Error("PCHUB", "Startup didn't finish: " + ex.Message + " (details in crash.log)");
        }

        _ready = true;
        if (StartupUrl is not null) _vm!.HandleOpenRequest(StartupUrl, "An app on this PC");
        while (_pendingUrls.Count > 0) _vm!.HandleOpenRequest(_pendingUrls.Dequeue(), "An app on this PC");
    }

    /// <summary>A later launch of PCHUB handed over a message (see SingleInstance).</summary>
    private void OnMessage(string msg)
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Show();
            _window.Activate();
        }

        if (!msg.StartsWith("OPEN ", StringComparison.Ordinal)) return;
        string url = msg[5..].Trim();
        if (url.Length >= 2048 || !SingleInstance.IsWebAddress(url)) return;

        // Wait until the app list is loaded, so a request for ComfyUI opens ComfyUI's own tab.
        if (_ready) _vm?.HandleOpenRequest(url, "An app on this PC");
        else if (_pendingUrls.Count < 10) _pendingUrls.Enqueue(url);
    }

    private void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep PCHUB running; log and show the error, but only once a minute if the same one repeats.
        string key = e.Exception.GetType().FullName + ":" + e.Exception.Message;
        if (key != _lastUiError || DateTime.UtcNow - _lastUiErrorAt > TimeSpan.FromMinutes(1))
        {
            _lastUiError = key;
            _lastUiErrorAt = DateTime.UtcNow;
            CrashLog.Write("UI exception", e.Exception);
            _log?.Error("PCHUB", "Something went wrong: " + e.Exception.Message + " (details in crash.log)");
        }

        e.Handled = true;
    }

    private void Shutdown()
    {
        _cts.Cancel();
        SingleInstance.Stop();
        _vm?.Shutdown();
        _monitor?.Dispose();
        _gpu?.Dispose();
        try { ToolStore.SaveSettings(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CrashLog.Write("Saving settings failed", ex); }
        _log?.Dispose();
    }
}

/// <summary>Used when GPU detection itself failed: everything else in PCHUB keeps working.</summary>
internal sealed class NoGpuMonitor(string note) : IGpuMonitor
{
    public string Name => "No GPU detected";
    public string Detail => "Sensors off";
    public string? Note { get; } = note;
    public bool HasSensors => false;
    public string MemoryLabel => "VRAM";
    public bool PerProcessIsGpuMemory => true;
    public ulong TotalBytes => 0;
    public GpuSample? ReadFast() => null;
    public GpuScanReading ReadScan(IReadOnlyDictionary<int, ProcInfo> processes) => new(new(), null, null);
    public ulong? UsedBytesNow() => null;
    public void Dispose() { }
}
