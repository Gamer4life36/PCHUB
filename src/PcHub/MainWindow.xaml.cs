using System.Windows;
using System.Windows.Interop;
using PcHub.Native;
using PcHub.Services;
using PcHub.ViewModels;
using PcHub.Views;

namespace PcHub;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        RestorePlacement();
        SourceInitialized += (_, _) => Win32.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Closing += (_, _) => SavePlacement();
        Loaded += (_, _) => AttachWebHost();
        Closed += (_, _) => _web?.Dispose();
    }

    private WebHost? _web;

    private void AttachWebHost()
    {
        if (_web is not null || DataContext is not MainViewModel vm) return;
        _web = new WebHost(WebHostGrid, WebPlaceholder, vm);
        _web.RuntimeChecked += ok =>
        {
            WaitingPanel.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            RuntimeMissingPanel.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    /// <summary>Only on the user's click: Microsoft's official WebView2 download page.</summary>
    private void GetWebView2_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default browser registered; nothing else to do.
        }
    }

    private void RestorePlacement()
    {
        double w = Math.Max(MinWidth, _settings.Width);
        double h = Math.Max(MinHeight, _settings.Height);
        Width = w;
        Height = h;

        // Only restore a position that is still on a connected screen.
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) &&
            vs.IntersectsWith(new Rect(_settings.Left, _settings.Top, Math.Min(w, 200), 60)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty) return;
        _settings.Left = r.Left;
        _settings.Top = r.Top;
        _settings.Width = r.Width;
        _settings.Height = r.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
    }
}
