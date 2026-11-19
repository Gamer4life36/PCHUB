using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PcHub.Platform;
using PcHub.Services;
using PcHub.ViewModels;

namespace PcHub.Views;

/// <summary>
/// Hosts the in-app web tabs: Edge WebView2 on Windows, Apple's WKWebView on macOS. One view per tab, created on
/// first view and kept alive while the tab is open, so switching back to ComfyUI doesn't reload it. Rules:
///  - Pages on this computer load inside PCHUB.
///  - Links or pop-ups to anywhere else are stopped; only ones the user clicked ask to open the browser.
///  - If the web view isn't available (WebView2 missing on an old Windows 10 PC), nothing opens by itself.
/// </summary>
public sealed class WebHost
{
    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private readonly Grid _host;
    private readonly Control _placeholder;
    private readonly MainViewModel _vm;
    private readonly Dictionary<string, NativeWebView> _views = new();
    private readonly HashSet<WebTabViewModel> _watched = new();
    private bool? _runtimeOk;

    public WebHost(Grid host, Control placeholder, MainViewModel vm)
    {
        _host = host;
        _placeholder = placeholder;
        _vm = vm;
        vm.PropertyChanged += OnVmChanged;
        vm.WebTabs.CollectionChanged += OnTabsChanged;
        vm.ReloadRequested += tab =>
        {
            try
            {
                if (_views.TryGetValue(tab.Key, out var wv)) wv.Refresh();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                vm.Log.Warn(tab.Title, "Couldn't reload: " + ex.Message);
            }
        };
        _ = SafeUpdateAsync();
    }

    public event Action<bool>? RuntimeChecked;

    /// <summary>
    /// macOS always has WKWebView. On Windows the Edge WebView2 runtime is needed; it is re-checked until found,
    /// so installing it works without restarting PCHUB.
    /// </summary>
    public bool RuntimeAvailable
    {
        get
        {
            if (_runtimeOk is true) return true;
            _runtimeOk = OperatingSystem.IsWindows() ? WebView2Installed() : !OperatingSystem.IsLinux() || WebKitGtkInstalled();
            return _runtimeOk.Value;
        }
    }

    /// <summary>Linux needs the WebKitGTK system library (libwebkit2gtk-4.1), which most desktops already have.</summary>
    private static bool WebKitGtkInstalled()
    {
        foreach (string lib in new[] { "libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37" })
        {
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(lib, out nint h))
            {
                System.Runtime.InteropServices.NativeLibrary.Free(h);
                return true;
            }
        }

        return false;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WebView2Installed()
    {
        string[] keys =
        [
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId,
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId,
        ];
        try
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                foreach (string k in keys)
                {
                    using var key = root.OpenSubKey(k);
                    if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true; // can't tell: let the control try
        }

        return false;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedTab) or nameof(MainViewModel.IsOverlayOpen)) _ = SafeUpdateAsync();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Drop the views of tabs that are gone (closed, or replaced after a tool-list reload).
        var keys = _vm.WebTabs.Select(t => t.Key).ToHashSet();
        foreach (var key in _views.Keys.Where(k => !keys.Contains(k)).ToList()) RemoveView(key);

        foreach (var tab in _watched.Where(t => !_vm.WebTabs.Contains(t)).ToList())
        {
            tab.ReadyChanged -= OnTabReadyChanged;
            tab.PropertyChanged -= OnTabPropertyChanged;
            _watched.Remove(tab);
        }

        foreach (var tab in _vm.WebTabs)
        {
            if (_watched.Add(tab))
            {
                tab.ReadyChanged += OnTabReadyChanged;
                tab.PropertyChanged += OnTabPropertyChanged;
            }
        }

        _ = SafeUpdateAsync();
    }

    private void OnTabReadyChanged() => Dispatcher.UIThread.Post(() => _ = SafeUpdateAsync());

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WebTabViewModel.Url) || sender is not WebTabViewModel tab) return;
        if (_views.TryGetValue(tab.Key, out var wv) && Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri)) wv.Navigate(uri);
    }

    private async Task SafeUpdateAsync()
    {
        try
        {
            Update();
            await Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("In-app viewer", ex);
            _vm.Log.Error("PCHUB", "The in-app viewer hit a problem: " + ex.Message);
        }
    }

    private void Update()
    {
        var tab = _vm.SelectedTab;
        bool showView = tab is not null && tab.IsReady && !_vm.IsOverlayOpen && RuntimeAvailable;

        // Native views draw above everything else, so they are hidden while a dialog is open.
        foreach (var (key, wv) in _views) wv.IsVisible = showView && key == tab!.Key;
        _placeholder.IsVisible = tab is not null && !showView && !_vm.IsOverlayOpen;
        RuntimeChecked?.Invoke(RuntimeAvailable);
        if (!showView || _views.ContainsKey(tab!.Key)) return;

        CreateView(tab);
    }

    private void CreateView(WebTabViewModel tab)
    {
        var wv = new NativeWebView();
        wv.EnvironmentRequested += (_, e) =>
        {
            // Browser profile (cookies, ComfyUI settings) lives with PCHUB's data, not next to the program.
            if (e is WindowsWebView2EnvironmentRequestedEventArgs win)
            {
                string folder = Path.Combine(AppPaths.Data, "webview2");
                Directory.CreateDirectory(folder);
                win.UserDataFolder = folder;
            }
        };

        wv.NavigationStarted += (_, e) =>
        {
            var uri = e.Request;
            if (uri is null || MainViewModel.IsLocal(uri) || uri.Scheme is "about" or "data" or "blob") return;

            e.Cancel = true; // leaving this computer is never automatic
            AskOrBlock(tab, uri);
        };

        wv.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // never pop a separate browser window by itself
            if (e.Request is { } uri)
            {
                if (MainViewModel.IsLocal(uri)) _vm.HandleOpenRequest(uri.AbsoluteUri, tab.Title);
                else AskOrBlock(tab, uri);
            }
        };

        wv.ProcessTerminated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // The viewer is gone: drop it so it is rebuilt the next time the tab is shown.
            _vm.Log.Warn(tab.Title, "The in-app viewer closed unexpectedly; it will reopen.");
            RemoveView(tab.Key);
            _ = SafeUpdateAsync();
        });

        _views[tab.Key] = wv;
        _host.Children.Add(wv);
        if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var start)) wv.Navigate(start);
    }

    private readonly Dictionary<string, DateTime> _lastAsk = new();

    /// <summary>
    /// A page trying to leave this computer gets at most one "open in your browser?" question every 3 seconds;
    /// anything more (e.g. a page redirecting in a loop) is just blocked and noted in History.
    /// </summary>
    private void AskOrBlock(WebTabViewModel tab, Uri uri)
    {
        var now = DateTime.UtcNow;
        if (_lastAsk.TryGetValue(tab.Key, out var last) && now - last < TimeSpan.FromSeconds(3))
        {
            _vm.Log.Info(tab.Title, $"Blocked the page from leaving this computer ({uri.Host}).");
            return;
        }

        _lastAsk[tab.Key] = now;
        Dispatcher.UIThread.Post(() => _vm.HandleOpenRequest(uri.AbsoluteUri, tab.Title));
    }

    private void RemoveView(string key)
    {
        if (!_views.Remove(key, out var wv)) return;
        _host.Children.Remove(wv);
        (wv as IDisposable)?.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in _views.Keys.ToList()) RemoveView(key);
    }
}
