using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PcHub.Platform;
using PcHub.Services;
using PcHub.ViewModels;
using PcHub.Views;

namespace PcHub;

public partial class MainWindow : Window, IDialogs
{
    private readonly AppSettings _settings;
    private WebHost? _web;

    // Needed by the Avalonia XAML loader.
    public MainWindow() : this(new AppSettings())
    {
    }

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Ui.Dialogs = this;
        RestorePlacement();
        Opened += (_, _) => AttachWebHost();
        Closing += (_, _) => SavePlacement();
        Closed += (_, _) => _web?.Dispose();
    }

    private void AttachWebHost()
    {
        if (_web is not null || DataContext is not MainViewModel vm) return;
        _web = new WebHost(WebHostGrid, WebPlaceholder, vm);
        _web.RuntimeChecked += ShowRuntime;
        ShowRuntime(_web.RuntimeAvailable); // the first check ran in the constructor, before this handler existed
    }

    private void ShowRuntime(bool ok)
    {
        if (!ok && Os.Current.IsLinux)
        {
            // Linux: the system's WebKitGTK library; nothing to download from a web page.
            RuntimeTitle.Text = "Showing apps inside PCHUB needs WebKitGTK";
            RuntimeText.Text = "Install it with your package manager, then restart PCHUB. Ubuntu, Debian, Mint: sudo apt install libwebkit2gtk-4.1-0 · " +
                               "Fedora: sudo dnf install webkit2gtk4.1 · Arch: sudo pacman -S webkit2gtk-4.1. Or open this app in your browser instead.";
            RuntimeButton.IsVisible = false;
        }

        WaitingPanel.IsVisible = ok;
        RuntimeMissingPanel.IsVisible = !ok;
    }

    // ---------------- window placement ----------------

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);

        // Only restore a position that is still on a connected screen.
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            var pos = new PixelPoint((int)_settings.Left, (int)_settings.Top);
            bool onScreen = Screens.All.Any(s => s.WorkingArea.Contains(pos + new PixelPoint(40, 40)));
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = pos;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState != WindowState.Normal) return;
        _settings.Left = Position.X;
        _settings.Top = Position.Y;
        _settings.Width = Width;
        _settings.Height = Height;
    }

    // ---------------- dialogs (IDialogs) ----------------

    public async Task<string?> PickProgramAsync()
    {
        var types = Os.Current.IsMac
            ? new[] { new FilePickerFileType("Apps and programs") { Patterns = ["*.app", "*.sh", "*"] } }
            : Os.Current.IsLinux
            ? new[] { new FilePickerFileType("Programs and scripts") { Patterns = ["*.AppImage", "*.appimage", "*.sh", "*"] } }
            : new[]
            {
                new FilePickerFileType("Programs and launchers") { Patterns = ["*.exe", "*.bat", "*.cmd", "*.vbs"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            };
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the program or launcher",
            AllowMultiple = false,
            FileTypeFilter = types,
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        IStorageFolder? start = null;
        if (startFolder is not null && Directory.Exists(startFolder))
        {
            start = await StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> AskApiKeyAsync()
    {
        var win = new ApiKeyWindow();
        bool ok = await win.ShowDialog<bool>(this);
        return ok ? win.Key : null;
    }

    /// <summary>Only on the user's click: Microsoft's official WebView2 download page.</summary>
    private void GetWebView2_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Os.Current.OpenWithShell("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CrashLog.Write("Couldn't open the WebView2 page", ex);
        }
    }
}
