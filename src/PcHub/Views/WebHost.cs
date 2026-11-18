using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PcHub.Services;
using PcHub.ViewModels;

namespace PcHub.Views;

/// <summary>
/// Hosts the in-app web tabs. One WebView2 per tab, created on first view and kept alive while the
/// tab is open, so switching back to ComfyUI doesn't reload it. Rules:
///  - Pages on this PC load inside PCHUB.
///  - Links or pop-ups to anywhere else are stopped and the user is asked before the browser opens.
///  - If the WebView2 runtime is missing, nothing opens by itself; the user gets buttons to choose.
/// </summary>
public sealed class WebHost
{
    private readonly Grid _host;
    private readonly FrameworkElement _placeholder;
    private readonly MainViewModel _vm;
    private readonly Dictionary<string, WebView2> _views = new();
    private readonly HashSet<WebTabViewModel> _watched = new();
    private Task<CoreWebView2Environment?>? _env;
    private bool? _runtimeOk;

    public WebHost(Grid host, FrameworkElement placeholder, MainViewModel vm)
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
                if (_views.TryGetValue(tab.Key, out var wv) && wv.CoreWebView2 is not null) wv.CoreWebView2.Reload();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
            {
                vm.Log.Warn(tab.Title, "Couldn't reload: " + ex.Message);
            }
        };
    }

    /// <summary>True when the Edge WebView2 runtime is installed (checked once).</summary>
    public bool RuntimeAvailable
    {
        get
        {
            if (_runtimeOk is not true) // re-checked until found, so installing WebView2 works without a restart
            {
                try
                {
                    _runtimeOk = !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
                }
                catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or BadImageFormatException or COMException)
                {
                    _runtimeOk = false;
                }
            }

            return _runtimeOk.Value;
        }
    }

    public event Action<bool>? RuntimeChecked;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedTab) or nameof(MainViewModel.IsOverlayOpen)) _ = SafeUpdateAsync();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Drop the views of tabs that are gone (closed, or replaced after a tool-list reload with a new key).
        var keys = _vm.WebTabs.Select(t => t.Key).ToHashSet();
        foreach (var key in _views.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            var wv = _views[key];
            _views.Remove(key);
            _host.Children.Remove(wv);
            wv.Dispose();
        }

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

    private void OnTabReadyChanged() => _ = SafeUpdateAsync();

    /// <summary>Never lets a viewer failure vanish: it is logged and the tab shows its waiting panel.</summary>
    private async Task SafeUpdateAsync()
    {
        try
        {
            await UpdateAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("In-app viewer", ex);
            _vm.Log.Error("PCHUB", "The in-app viewer hit a problem: " + ex.Message);
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WebTabViewModel.Url) || sender is not WebTabViewModel tab) return;
        if (_views.TryGetValue(tab.Key, out var wv) && wv.CoreWebView2 is not null &&
            Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri))
        {
            wv.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
    }

    private async Task UpdateAsync()
    {
        var tab = _vm.SelectedTab;
        bool showView = tab is not null && tab.IsReady && !_vm.IsOverlayOpen && RuntimeAvailable;

        foreach (var (key, wv) in _views)
        {
            wv.Visibility = showView && key == tab!.Key ? Visibility.Visible : Visibility.Collapsed;
        }

        _placeholder.Visibility = tab is not null && !showView && !_vm.IsOverlayOpen ? Visibility.Visible : Visibility.Collapsed;
        RuntimeChecked?.Invoke(RuntimeAvailable);
        if (!showView || _views.ContainsKey(tab!.Key)) return;

        var view = await CreateViewAsync(tab);
        if (view is null) return;
        // Selection may have changed while the view was being created.
        view.Visibility = _vm.SelectedTab == tab && !_vm.IsOverlayOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<WebView2?> CreateViewAsync(WebTabViewModel tab)
    {
        _env ??= CreateEnvironmentAsync();
        var env = await _env;
        if (env is null) _env = null; // allow a retry next time instead of caching the failure
        if (env is null || _views.ContainsKey(tab.Key) || !_vm.WebTabs.Contains(tab)) return null;

        var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0E, 0x10, 0x14) };
        _views[tab.Key] = wv;
        _host.Children.Add(wv);
        try
        {
            await wv.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            _vm.Log.Error(tab.Title, "Couldn't create the in-app viewer: " + ex.Message);
            _views.Remove(tab.Key);
            _host.Children.Remove(wv);
            wv.Dispose();
            return null;
        }

        var core = wv.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = true;
        core.DocumentTitleChanged += (_, _) =>
        {
            // Keep the tool's name on the tab; only tabs without a tool use the page title.
            if (tab.Tool is null && !string.IsNullOrWhiteSpace(core.DocumentTitle)) tab.Title = core.DocumentTitle;
        };
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && (MainViewModel.IsLocal(uri) || uri.Scheme is "about" or "data" or "blob"))
            {
                return;
            }

            e.Cancel = true; // leaving this PC is never automatic
            // Only a link the user actually clicked gets a prompt; a page redirecting by itself is just blocked.
            if (e.IsUserInitiated) _vm.HandleOpenRequest(e.Uri, tab.Title);
            else _vm.Log.Info(tab.Title, $"Blocked the page from leaving this PC on its own ({e.Uri}).");
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true; // never pop a separate browser window by itself
            if (e.IsUserInitiated) _vm.HandleOpenRequest(e.Uri, tab.Title);
            else _vm.Log.Info(tab.Title, $"Blocked a pop-up the page opened on its own ({e.Uri}).");
        };
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                // The whole viewer is gone: drop it so it is rebuilt the next time the tab is shown.
                _vm.Log.Warn(tab.Title, "The in-app viewer closed unexpectedly; it will reopen.");
                _host.Dispatcher.InvokeAsync(() =>
                {
                    if (_views.Remove(tab.Key, out var dead))
                    {
                        _host.Children.Remove(dead);
                        dead.Dispose();
                    }

                    _env = null;
                    _ = SafeUpdateAsync();
                });
            }
            else
            {
                _vm.Log.Warn(tab.Title, $"Part of the in-app viewer stopped ({e.ProcessFailedKind}). Press Reload if the page looks wrong.");
            }
        };

        if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var start)) core.Navigate(start.AbsoluteUri);
        return wv;
    }

    private async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            // Browser profile (cookies, ComfyUI settings) lives with PCHUB's data, not next to the exe.
            string folder = Path.Combine(AppPaths.Data, "webview2");
            Directory.CreateDirectory(folder);
            return await CoreWebView2Environment.CreateAsync(null, folder);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or IOException or UnauthorizedAccessException)
        {
            _runtimeOk = false;
            _vm.Log.Error("PCHUB", "The in-app viewer isn't available: " + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var wv in _views.Values) wv.Dispose();
        _views.Clear();
    }
}
