using Avalonia;

namespace PcHub;

internal static class Program
{
    /// <summary>Entry point. Hands off to an already running PCHUB when there is one (see SingleInstance).</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (!SingleInstance.TryBecomeFirst(args, out string? startupUrl)) return 0;

        try
        {
            App.StartupUrl = startupUrl;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
