using System.Windows;
using System.Windows.Threading;
using PcHub.Services;
using PcHub.ViewModels;

namespace PcHub;

public partial class App : Application
{
    private const string MutexName = @"Local\PCHUB.SingleInstance";
    private const string ActivateEventName = @"Local\PCHUB.Activate";

    private Mutex? _single;
    private EventWaitHandle? _activate;
    private GpuContext? _gpu;
    private MonitorService? _monitor;
    private ActivityLog? _log;
    private MainViewModel? _vm;
    private AppSettings _settings = new();

    /// <summary>A web address to show, from "PCHUB.exe &lt;url&gt;" (a tool's browser request) or "PCHUB.exe --open &lt;url&gt;".</summary>
    private string? _startupUrl;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool fromTool = e.Args.Length >= 1 && IsWebAddress(e.Args[0]);
        _startupUrl = fromTool ? e.Args[0]
            : e.Args.Length >= 2 && e.Args[0] == "--open" && IsWebAddress(e.Args[1]) ? e.Args[1] : null;

        // One copy only: a second launch hands over its URL (if any) and brings the first window forward.
        _single = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (_startupUrl is not null) QueueOpenRequest(_startupUrl);
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ActivateEventName);
                ev.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* first copy still starting; it reads the queue on start */ }

            Shutdown();
            return;
        }

        if (fromTool)
        {
            // A tool asked to "open its browser" while PCHUB wasn't running. Python waits for the browser
            // command to finish, so start PCHUB separately and return at once instead of blocking the tool.
            _single.ReleaseMutex();
            _single.Dispose();
            _single = null;
            if (Environment.ProcessPath is { } self)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(self) { UseShellExecute = false };
                psi.ArgumentList.Add("--open");
                psi.ArgumentList.Add(_startupUrl!);
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write("Unhandled (fatal)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            CrashLog.Write("Unobserved task", a.Exception);
            a.SetObserved();
        };

        base.OnStartup(e);

        Native.Nvml.RegisterResolver();
        _settings = ToolStore.LoadSettings();
        _gpu = GpuContext.CreateSafe(_settings.AdapterLuid);
        _monitor = new MonitorService(_gpu, _settings.LowPower);
        _log = new ActivityLog(Dispatcher);
        _vm = new MainViewModel(_gpu, _monitor, _log, _settings, Dispatcher);

        var window = new MainWindow(_settings) { DataContext = _vm };
        MainWindow = window;
        window.Show();

        StartActivationListener(window);

        // Sample gently while minimized: nobody is looking, and a laptop GPU can go back to sleep.
        window.StateChanged += (_, _) => _monitor!.Background = window.WindowState == WindowState.Minimized;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await _vm!.InitializeAsync();
        _ready = true;
        if (_startupUrl is not null) _vm.HandleOpenRequest(_startupUrl, "An app on this PC");
        DrainOpenRequests();
    }

    private static bool IsWebAddress(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string RequestsFolder => Path.Combine(AppPaths.Data, "requests");

    /// <summary>Second copy → first copy hand-off: one small file per URL (only web addresses are accepted).</summary>
    private static void QueueOpenRequest(string url)
    {
        try
        {
            Directory.CreateDirectory(RequestsFolder);
            File.WriteAllText(Path.Combine(RequestsFolder, Guid.NewGuid().ToString("N") + ".url"), url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Includes the data folder being unusable; this copy is about to exit anyway.
            try { CrashLog.Write("Couldn't pass a web address to the running PCHUB", ex); } catch (Exception) { /* nowhere left to report */ }
        }
    }

    private bool _ready;

    private void DrainOpenRequests()
    {
        // Wait until the app list is loaded, so a request for ComfyUI opens ComfyUI's own tab.
        if (!_ready || _vm is null || !Directory.Exists(RequestsFolder)) return;
        foreach (string file in Directory.EnumerateFiles(RequestsFolder, "*.url").OrderBy(File.GetCreationTimeUtc).Take(10).ToList())
        {
            string url;
            DateTime created;
            try
            {
                var info = new FileInfo(file);
                created = info.LastWriteTimeUtc;
                if (info.Length > 4096)
                {
                    File.Delete(file); // a web address is never this big
                    continue;
                }

                url = File.ReadAllText(file).Trim();
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Old requests (e.g. left over from a crash) are dropped rather than suddenly opened later.
            if (url.Length < 2048 && IsWebAddress(url) && DateTime.UtcNow - created < TimeSpan.FromMinutes(2))
            {
                _vm.HandleOpenRequest(url, "An app on this PC");
            }
        }
    }

    private void StartActivationListener(Window window)
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (_activate.WaitOne())
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                        window.Show();
                        window.Activate();
                        DrainOpenRequests();
                    });
                }
            }
            catch (ObjectDisposedException) { /* app closing */ }
        })
        { IsBackground = true, Name = "PCHUB activation" };
        thread.Start();
    }

    private string? _lastUiError;
    private DateTime _lastUiErrorAt;

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the hub running; log and show the error, but only once a minute if the same one repeats.
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _vm?.Shutdown();
            _monitor?.Dispose();
            _gpu?.Dispose();
            try { ToolStore.SaveSettings(_settings); }
            catch (IOException ex) { CrashLog.Write("Saving settings failed", ex); }
            _log?.Dispose();
        }
        finally
        {
            _activate?.Dispose();
            if (_single is not null)
            {
                try { _single.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
                _single.Dispose();
            }

            base.OnExit(e);
        }
    }
}
