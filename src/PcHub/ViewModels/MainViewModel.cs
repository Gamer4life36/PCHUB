using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PcHub.Controls;
using PcHub.Services;

namespace PcHub.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int HistorySeconds = 120;
    private readonly GpuContext _gpu;
    private readonly MonitorService _monitor;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private int _applyPending;

    private string _vramUsed = "—", _vramTotal = "", _load = "—", _temp = "—", _tempSub = "", _power = "—", _powerSub = "";
    private double _vramPercent;
    private double _powerMax = 100;
    private string _scanText = "";
    private string? _counterNote;
    private string _freeStatus = "";
    private bool _freeing;
    private bool _confirmOpen;
    private string _confirmTitle = "", _confirmText = "", _confirmButton = "";
    private Func<Task>? _confirmAction;
    private bool _addOpen;
    private string _addName = "", _addProgram = "", _addArgs = "", _addPort = "", _addUrl = "";
    private bool _addHidden;
    private string? _addError;

    public MainViewModel(GpuContext gpu, MonitorService monitor, ActivityLog log, AppSettings settings, Dispatcher ui)
    {
        _gpu = gpu;
        _monitor = monitor;
        _settings = settings;
        _ui = ui;
        Log = log;

        FreeGpuCommand = new RelayCommand(() => _ = AskFreeGpuAsync(), () => !_freeing);
        ConfirmCommand = new RelayCommand(() => _ = RunConfirmAsync());
        CancelConfirmCommand = new RelayCommand(CloseConfirm);
        // Start/Stop visibility already follows the tool's state; the handlers re-check it.
        StartCommand = new RelayCommand(p => { if (p is ToolViewModel { CanStart: true } t) StartTool(t); });
        StopCommand = new RelayCommand(p => { if (p is ToolViewModel { CanStop: true } t) _ = StopToolAsync(t, false); });
        OpenUrlCommand = new RelayCommand(p => { if (p is ToolViewModel { Definition.Url: { } url }) ShellOpen(url); });
        OpenLogCommand = new RelayCommand(p => { if (p is ToolViewModel t) OpenToolLog(t); });
        OpenFolderCommand = new RelayCommand(p => { if (p is ToolViewModel t) OpenToolFolder(t); });
        RemoveToolCommand = new RelayCommand(p => { if (p is ToolViewModel t) AskRemove(t); });
        RescanCommand = new RelayCommand(() => _ = RescanAsync());
        EditToolsCommand = new RelayCommand(() => ShellOpen("notepad.exe", $"\"{AppPaths.ToolsFile}\""));
        ReloadToolsCommand = new RelayCommand(ReloadTools);
        OpenDataCommand = new RelayCommand(() => ShellOpen(AppPaths.Data));
        AddAppCommand = new RelayCommand(OpenAddDialog);
        BrowseProgramCommand = new RelayCommand(BrowseProgram);
        SaveAddCommand = new RelayCommand(SaveAdd);
        CancelAddCommand = new RelayCommand(() => AddOpen = false);

        if (!gpu.HasNvml)
        {
            _temp = "n/a";
            _tempSub = "needs an NVIDIA card";
            _power = "n/a";
            _powerSub = "needs an NVIDIA card";
        }

        SetUpDrives();
        SetUpWebTabs();
        SetUpSuggestions();
        _monitor.SnapshotReady += OnSnapshot;
        _monitor.Faulted += ex => Log.Error("PCHUB", "Monitoring hit an error: " + ex.Message);
    }

    public ActivityLog Log { get; }
    public ObservableCollection<DriveViewModel> Drives { get; } = new();
    public ICommand OptimizeDrivesCommand { get; private set; } = null!;
    public ICommand RefreshDrivesCommand { get; private set; } = null!;
    private DispatcherTimer? _driveTimer;
    private bool _drivesBusy;

    private void SetUpDrives()
    {
        OptimizeDrivesCommand = new RelayCommand(() =>
        {
            try { DriveService.OpenOptimizeDrives(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Error("Drives", "Couldn't open Optimize Drives: " + ex.Message);
            }
        });
        RefreshDrivesCommand = new RelayCommand(() => _ = RefreshDrivesAsync());
        OptimizeDriveCommand = new RelayCommand(p => { if (p is DriveViewModel { CanOptimize: true } d) AskOptimize(d); });
        _driveTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => _ = RefreshDrivesAsync(), _ui);
        _driveTimer.Start();
    }

    public ICommand OptimizeDriveCommand { get; private set; } = null!;

    private void AskOptimize(DriveViewModel d)
    {
        string what = d.IsSsd
            ? $"{d.Letter} is an SSD, so Windows will run a retrim (tells the SSD which space is free). It usually takes under a minute."
            : $"Windows will defragment {d.Letter}. On a hard drive this can take a while; you can keep using the PC.";
        ShowConfirm($"Optimize {d.Letter}?",
            what + Environment.NewLine + Environment.NewLine + "This uses Windows' own optimizer, which needs admin approval. Windows will ask you.",
            "Optimize",
            () => OptimizeAsync(d));
    }

    private async Task OptimizeAsync(DriveViewModel d)
    {
        int? before = d.FragmentationPercent;
        System.Diagnostics.Process? proc;
        try
        {
            proc = OptimizeRunner.Start(d.Letter);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ArgumentException)
        {
            Log.Error("Drives", $"Couldn't start the optimizer for {d.Letter}: {ex.Message}");
            return;
        }

        if (proc is null)
        {
            Log.Info("Drives", $"Optimizing {d.Letter} cancelled at the admin prompt.");
            return;
        }

        Log.Info("Drives", $"Optimizing {d.Letter}…");
        d.Progress = 0;
        d.StageText = "Starting…";
        d.IsOptimizing = true;
        try
        {
            using (proc)
            {
                while (!HasExited(proc))
                {
                    await Task.Delay(400);
                    if (OptimizeRunner.ReadProgress(d.Letter) is var (stage, pct))
                    {
                        d.StageText = $"{stage} {pct}%";
                        d.Progress = pct / 100.0;
                    }
                }
            }

            var final = OptimizeRunner.ReadProgress(d.Letter);
            await RefreshDrivesAsync();
            if (final is (_, 100))
            {
                string change = before is int b && d.FragmentationPercent is int a && !d.IsSsd ? $" ({b}% → {a}% fragmented)" : "";
                Log.Info("Drives", $"Optimized {d.Letter}{change}.");
            }
            else
            {
                string last = OptimizeRunner.LastLine(d.Letter);
                Log.Warn("Drives", $"Optimizing {d.Letter} ended early." + (last.Length > 0 ? " " + last : ""));
            }
        }
        finally
        {
            d.IsOptimizing = false;
            d.StageText = "";
        }
    }

    private static bool HasExited(System.Diagnostics.Process p)
    {
        try { return p.HasExited; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No rights to query the elevated process: fall back to "is that PID still alive?"
            try { using var again = System.Diagnostics.Process.GetProcessById(p.Id); return again.HasExited; }
            catch (ArgumentException) { return true; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>For screenshots and testing only: PCHUB_DEMO_OPTIMIZE=1 animates the first drive with fake progress.</summary>
    private async Task DemoOptimizeAsync()
    {
        await Task.Delay(1500);
        var d = Drives.FirstOrDefault();
        if (d is null) return;
        d.ShowDemoFragments(0.12);
        d.IsOptimizing = true;
        for (int i = 0; i <= 100; i++)
        {
            d.Progress = i / 100.0;
            d.StageText = $"Preview {i}%";
            await Task.Delay(120);
        }

        d.IsOptimizing = false;
        d.StageText = "";
    }

    private async Task RefreshDrivesAsync()
    {
        if (_drivesBusy) return;
        _drivesBusy = true;
        try
        {
            var list = await Task.Run(DriveService.Read);
            var byLetter = Drives.ToDictionary(d => d.Letter);
            for (int i = 0; i < list.Count; i++)
            {
                if (!byLetter.Remove(list[i].Letter, out var vm))
                {
                    vm = new DriveViewModel(list[i].Letter);
                    Drives.Insert(Math.Min(i, Drives.Count), vm);
                }

                vm.Update(list[i]);
            }

            foreach (var gone in byLetter.Values) Drives.Remove(gone);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Drives", "Couldn't read drives: " + ex.Message);
        }
        finally
        {
            _drivesBusy = false;
        }
    }

    // ---------- in-app web tabs ----------

    private WebTabViewModel? _selectedTab;

    public ObservableCollection<WebTabViewModel> WebTabs { get; } = new();
    public bool HasWebTabs => WebTabs.Count > 0;

    /// <summary>The web tab on screen, or null when the dashboard is showing.</summary>
    public WebTabViewModel? SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            if (value is not null) _storeOpen = false;
            if (_selectedTab is not null) _selectedTab.IsSelected = false;
            if (!Set(ref _selectedTab, value)) return;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(IsDashboard));
            Raise(nameof(IsWebView));
            Raise(nameof(IsStore));
            Raise(nameof(ShowCompactStats));
            Raise(nameof(ShowGpuNote));
            Raise(nameof(CompactStats));
        }
    }

    private bool _storeOpen;

    public bool IsDashboard => _selectedTab is null && !_storeOpen;
    public bool IsWebView => _selectedTab is not null;
    public bool IsStore => _storeOpen && _selectedTab is null;

    /// <summary>"Get apps": the online catalog of image, video and chat apps.</summary>
    public StoreViewModel Store { get; private set; } = null!;
    public ICommand ShowStoreCommand { get; private set; } = null!;

    private void ShowPage(bool store, WebTabViewModel? tab)
    {
        _storeOpen = store && tab is null;
        if (ReferenceEquals(_selectedTab, tab))
        {
            Raise(nameof(IsDashboard));
            Raise(nameof(IsStore));
            Raise(nameof(ShowGpuNote));
            Raise(nameof(ShowCompactStats));
            Raise(nameof(CompactStats));
            return;
        }

        SelectedTab = tab;
    }
    public bool ShowGpuNote => IsDashboard && HasGpuNote;

    /// <summary>Hides the native web view while a dialog is open (it would otherwise draw over the dialog).</summary>
    public bool IsOverlayOpen => ConfirmOpen || AddOpen;

    public ICommand OpenInAppCommand { get; private set; } = null!;
    public ICommand ShowDashboardCommand { get; private set; } = null!;
    public ICommand SelectTabCommand { get; private set; } = null!;
    public ICommand CloseTabCommand { get; private set; } = null!;
    public ICommand OpenTabInBrowserCommand { get; private set; } = null!;
    public ICommand StartTabToolCommand { get; private set; } = null!;

    /// <summary>Raised when the user presses Reload on a web tab; the view reloads that page.</summary>
    public event Action<WebTabViewModel>? ReloadRequested;
    public ICommand ReloadTabCommand { get; private set; } = null!;

    private void SetUpWebTabs()
    {
        OpenInAppCommand = new RelayCommand(p => { if (p is ToolViewModel t) OpenToolInApp(t); });
        ShowDashboardCommand = new RelayCommand(() => ShowPage(false, null));
        ShowStoreCommand = new RelayCommand(() => ShowPage(true, null));
        Store = new StoreViewModel(_settings, Log, _gpu.Adapter?.Vendor.ToLowerInvariant() is "nvidia" or "amd" or "intel" ? _gpu.Adapter!.Vendor.ToLowerInvariant() : (_gpu.HasNvml ? "nvidia" : "none"), ShowConfirm);
        SelectTabCommand = new RelayCommand(p => { if (p is WebTabViewModel w) ShowPage(false, w); });
        CloseTabCommand = new RelayCommand(p => { if (p is WebTabViewModel w) CloseTab(w); });
        OpenTabInBrowserCommand = new RelayCommand(p => { if (p is WebTabViewModel w) OpenInBrowser(w.Url); });
        StartTabToolCommand = new RelayCommand(p => { if (p is WebTabViewModel { Tool: { CanStart: true } t }) StartTool(t); });
        ReloadTabCommand = new RelayCommand(p => { if (p is WebTabViewModel w) ReloadRequested?.Invoke(w); });
        WebTabs.CollectionChanged += (_, _) => Raise(nameof(HasWebTabs));
    }

    private void OpenToolInApp(ToolViewModel t)
    {
        if (string.IsNullOrWhiteSpace(t.Definition.Url)) return;
        var tab = WebTabs.FirstOrDefault(w => w.Key == t.Id);
        if (tab is null)
        {
            tab = new WebTabViewModel(t.Id, t.Name, t.Definition.Url, t);
            WebTabs.Add(tab);
        }

        SelectedTab = tab;
    }

    private void CloseTab(WebTabViewModel w)
    {
        int i = WebTabs.IndexOf(w);
        if (i < 0) return;
        if (SelectedTab == w) SelectedTab = null;
        w.Detach();
        WebTabs.RemoveAt(i);
    }

    /// <summary>Re-points open tabs at the new tool objects after the tool list reloads.</summary>
    private void RebindTabs()
    {
        var selectedKey = SelectedTab?.Key;
        foreach (var tab in WebTabs.ToList())
        {
            if (tab.Tool is null) continue;
            var tool = Tools.FirstOrDefault(t => t.Id == tab.Key);
            int i = WebTabs.IndexOf(tab);
            tab.Detach();
            if (tool is null || string.IsNullOrWhiteSpace(tool.Definition.Url))
            {
                if (SelectedTab == tab) SelectedTab = null;
                WebTabs.RemoveAt(i);
                continue;
            }

            var fresh = new WebTabViewModel(tab.Key, tool.Name, tool.Definition.Url, tool);
            WebTabs[i] = fresh;
            if (selectedKey == tab.Key) SelectedTab = fresh;
        }
    }

    /// <summary>
    /// A page or a tool asked to open a URL. Pages on this PC open in a PCHUB tab; anything else
    /// opens in the user's browser only after they say yes.
    /// </summary>
    public void HandleOpenRequest(string url, string source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Log.Warn("PCHUB", $"Ignored a request from {source} to open \"{url}\" (not a web address).");
            return;
        }

        if (IsLocal(uri))
        {
            // Pages of the user's own apps open straight away; any other local page asks first.
            var tool = Tools.FirstOrDefault(t => t.Definition.Port is > 0 && t.Definition.Port == uri.Port);
            if (tool is null)
            {
                ShowConfirm("Open a page from this PC?",
                    $"{source} wants to show a local web page that doesn't belong to one of your apps:{Environment.NewLine}{Environment.NewLine}" +
                    $"{uri.AbsoluteUri}{Environment.NewLine}{Environment.NewLine}It will open in a PCHUB tab.",
                    "Open it",
                    () =>
                    {
                        OpenLocalTab(uri, null);
                        return Task.CompletedTask;
                    });
                return;
            }

            OpenLocalTab(uri, tool);
            return;
        }

        // Show the real host on its own line (IdnHost spells out look-alike international domains).
        ShowConfirm("Open in your browser?",
            $"{source} wants to open a web page outside this PC.{Environment.NewLine}{Environment.NewLine}" +
            $"Site: {uri.IdnHost}{Environment.NewLine}Address: {uri.AbsoluteUri}{Environment.NewLine}{Environment.NewLine}" +
            "It will open in your default browser.",
            "Open in browser",
            () =>
            {
                OpenInBrowser(uri.AbsoluteUri);
                return Task.CompletedTask;
            });
    }

    private void OpenLocalTab(Uri uri, ToolViewModel? tool)
    {
        string key = tool?.Id ?? uri.GetLeftPart(UriPartial.Authority);
        var tab = WebTabs.FirstOrDefault(w => w.Key == key);
        if (tab is null)
        {
            tab = new WebTabViewModel(key, tool?.Name ?? uri.Authority, uri.AbsoluteUri, tool);
            WebTabs.Add(tab);
        }
        else if (!string.Equals(tab.Url, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            tab.Url = uri.AbsoluteUri;
        }

        ShowPage(false, tab);
    }

    public static bool IsLocal(Uri uri) =>
        uri.IsLoopback || uri.Host is "0.0.0.0" or "[::]" or "::";

    /// <summary>Only ever called from an explicit user action (a button or a confirmed dialog).</summary>
    private void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        ShellOpen(uri.AbsoluteUri);
    }

    public bool ShowCompactStats => !IsDashboard;
    public string CompactStats => $"VRAM {VramUsed} {VramTotal.Replace("of ", "/ ", StringComparison.Ordinal)}  ·  GPU {Load}  ·  {Temp}";

    public ObservableCollection<ToolViewModel> Tools { get; } = new();
    public ObservableCollection<GpuProcViewModel> GpuProcesses { get; } = new();

    public Series VramSeries { get; } = new(HistorySeconds);
    public Series LoadSeries { get; } = new(HistorySeconds);
    public Series TempSeries { get; } = new(HistorySeconds);
    public Series PowerSeries { get; } = new(HistorySeconds);

    public string GpuName => _gpu.Name;
    public string GpuDetail => _gpu.Detail;
    public string? GpuNote => _counterNote ?? _gpu.Note;
    public bool HasGpuNote => !string.IsNullOrEmpty(GpuNote);
    public bool HasSensors => _gpu.HasNvml;
    public string DataLocation => AppPaths.IsPortable ? $"Portable · {AppPaths.Data}" : AppPaths.Data;

    public string VramUsed { get => _vramUsed; private set => Set(ref _vramUsed, value); }
    public string VramTotal { get => _vramTotal; private set => Set(ref _vramTotal, value); }
    public double VramPercent { get => _vramPercent; private set => Set(ref _vramPercent, value); }
    public string Load { get => _load; private set => Set(ref _load, value); }
    public string Temp { get => _temp; private set => Set(ref _temp, value); }
    public string TempSub { get => _tempSub; private set => Set(ref _tempSub, value); }
    public string Power { get => _power; private set => Set(ref _power, value); }
    public string PowerSub { get => _powerSub; private set => Set(ref _powerSub, value); }
    public double PowerMax { get => _powerMax; private set => Set(ref _powerMax, value); }
    public string ScanText { get => _scanText; private set => Set(ref _scanText, value); }
    public string FreeStatus { get => _freeStatus; private set => Set(ref _freeStatus, value); }
    public bool HasTools => Tools.Count > 0;
    public bool HasNoTools => Tools.Count == 0;

    public bool LowPower
    {
        get => _settings.LowPower;
        set
        {
            if (_settings.LowPower == value) return;
            _settings.LowPower = value;
            _monitor.LowPower = value;
            Raise();
            Log.Info("PCHUB", value ? "Low-power mode on: sampling every 2 s." : "Low-power mode off: sampling every second.");
        }
    }

    public bool ConfirmOpen { get => _confirmOpen; set { if (Set(ref _confirmOpen, value)) Raise(nameof(IsOverlayOpen)); } }
    public string ConfirmTitle { get => _confirmTitle; private set => Set(ref _confirmTitle, value); }
    public string ConfirmText { get => _confirmText; private set => Set(ref _confirmText, value); }
    public string ConfirmButton { get => _confirmButton; private set => Set(ref _confirmButton, value); }

    public bool AddOpen { get => _addOpen; set { if (Set(ref _addOpen, value)) Raise(nameof(IsOverlayOpen)); } }
    public string AddName { get => _addName; set => Set(ref _addName, value); }
    public string AddProgram { get => _addProgram; set => Set(ref _addProgram, value); }
    public string AddArgs { get => _addArgs; set => Set(ref _addArgs, value); }
    public string AddPort { get => _addPort; set => Set(ref _addPort, value); }
    public string AddUrl { get => _addUrl; set => Set(ref _addUrl, value); }
    public bool AddHidden { get => _addHidden; set => Set(ref _addHidden, value); }
    public string? AddError { get => _addError; private set { if (Set(ref _addError, value)) Raise(nameof(HasAddError)); } }
    public bool HasAddError => !string.IsNullOrEmpty(_addError);

    public ICommand FreeGpuCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand CancelConfirmCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenUrlCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveToolCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand EditToolsCommand { get; }
    public ICommand ReloadToolsCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand AddAppCommand { get; }
    public ICommand BrowseProgramCommand { get; }
    public ICommand SaveAddCommand { get; }
    public ICommand CancelAddCommand { get; }

    public async Task InitializeAsync()
    {
        if (_gpu.Note is not null) Log.Warn("GPU", _gpu.Note);
        _monitor.Start();
        await RefreshDrivesAsync();
        if (Environment.GetEnvironmentVariable("PCHUB_DEMO_OPTIMIZE") == "1") _ = DemoOptimizeAsync();

        try
        {
            var excludes = _settings.ScanExcludes.ToList();
            var result = await Task.Run(() => ToolStore.Load(excludes));
            ApplyTools(result.Tools, result.Problems);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Loading tools at startup", ex);
            Log.Error("Tools", ex.Message + " Fix the file, then press Reload.");
        }
    }

    private void ApplyTools(List<ToolDefinition> defs, List<string> problems)
    {
        Tools.Clear();
        foreach (var d in defs) Tools.Add(new ToolViewModel(d, Log));
        foreach (string p in problems) Log.Warn("Tools", p);
        _monitor.SetTools(defs);
        Raise(nameof(HasTools));
        Raise(nameof(HasNoTools));
        Raise(nameof(ShowEmptyState));
        RebindTabs();
    }

    // ---------- monitoring ----------

    private void OnSnapshot(MonitorSnapshot snap)
    {
        // Coalesce: keep only the newest GPU sample, but never lose a process scan (tool state depends on it).
        Volatile.Write(ref _latestGpu, snap.Gpu);
        if (snap.Processes is not null) Volatile.Write(ref _latestPart, snap.Processes);
        if (Interlocked.Exchange(ref _applyPending, 1) == 1) return;
        _ui.InvokeAsync(() =>
        {
            Volatile.Write(ref _applyPending, 0);
            var gpu = Volatile.Read(ref _latestGpu);
            var part = Interlocked.Exchange(ref _latestPart, null);
            Apply(new MonitorSnapshot(gpu, part));
        }, DispatcherPriority.Background);
    }

    private GpuSample? _latestGpu;
    private ProcessPart? _latestPart;

    private void Apply(MonitorSnapshot snap)
    {
        if (snap.Gpu is { } g)
        {
            double total = g.TotalBytes > 0 ? g.TotalBytes : _gpu.TotalBytes;
            VramUsed = Format.Gigs(g.UsedBytes);
            VramTotal = total > 0 ? $"of {Format.Gigs(total)} GB" : "";
            VramPercent = total > 0 ? Math.Clamp(g.UsedBytes / total * 100, 0, 100) : 0;
            VramSeries.Push(VramPercent);
            Load = $"{g.LoadPercent}%";
            LoadSeries.Push(g.LoadPercent);

            if (g.TemperatureC is int t)
            {
                Temp = $"{t}°C";
                TempSub = g.FanPercent is int fan ? $"fan {fan}%" : "";
                TempSeries.Push(t);
            }

            if (g.PowerWatts is double w)
            {
                Power = $"{w:0} W";
                PowerSub = (g.PowerLimitWatts is double lim ? $"limit {lim:0} W" : "") +
                           (g.CoreMhz is int mhz ? $"  ·  {mhz} MHz" : "");
                if (g.PowerLimitWatts is double l && l > 0) PowerMax = l;
                PowerSeries.Push(w);
            }

            if (!IsDashboard) Raise(nameof(CompactStats));
        }

        if (snap.Processes is { } part)
        {
            foreach (var tool in Tools)
            {
                if (part.Tools.TryGetValue(tool.Id, out var st)) tool.Update(st);
            }

            SyncProcesses(part.GpuProcesses);
            ScanText = $"Scan {part.ScanMs:0} ms";
            if (_counterNote != part.CounterError)
            {
                _counterNote = part.CounterError;
                Raise(nameof(GpuNote));
                Raise(nameof(HasGpuNote));
                Raise(nameof(ShowGpuNote));
                if (part.CounterError is not null) Log.Warn("GPU", part.CounterError);
            }
        }
    }

    private void SyncProcesses(IReadOnlyList<GpuProcRow> rows)
    {
        double total = _gpu.TotalBytes;
        var names = Tools.ToDictionary(t => t.Id, t => t.Name);
        var existing = new Dictionary<(int, long), GpuProcViewModel>();
        foreach (var vm in GpuProcesses) existing[(vm.Pid, vm.CreateTime)] = vm;

        var ordered = new List<GpuProcViewModel>(rows.Count);
        foreach (var r in rows)
        {
            if (!existing.Remove((r.Proc.Pid, r.Proc.CreateTime), out var vm))
            {
                vm = new GpuProcViewModel(r.Proc.Pid, r.Proc.CreateTime, r.Proc.Name, r.Proc.Path);
            }

            string owner = r.OwnerToolId is not null && names.TryGetValue(r.OwnerToolId, out var n) ? n : "";
            vm.Update(r.Usage.DedicatedBytes, r.Usage.UtilPercent, owner, total);
            ordered.Add(vm);
        }

        for (int i = GpuProcesses.Count - 1; i >= 0; i--)
        {
            if (existing.ContainsKey((GpuProcesses[i].Pid, GpuProcesses[i].CreateTime))) GpuProcesses.RemoveAt(i);
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            int at = GpuProcesses.IndexOf(ordered[i]);
            if (at < 0) GpuProcesses.Insert(i, ordered[i]);
            else if (at != i) GpuProcesses.Move(at, i);
        }
    }

    // ---------- tools ----------

    private void ReloadTools()
    {
        try
        {
            var (defs, problems) = ToolStore.Load(_settings.ScanExcludes);
            ApplyTools(defs, problems);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Loading tools.json", ex);
            Log.Error("Tools", ex.Message + " Fix the file, then press Reload.");
        }
    }

    private void StartTool(ToolViewModel t)
    {
        try
        {
            var p = ToolController.Start(t.Definition);
            t.MarkLaunched(p);
            Log.Info(t.Name, "Starting…");
            _monitor.RequestScan();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            t.Error = ex.Message;
            Log.Error(t.Name, "Couldn't start: " + ex.Message);
        }
    }

    private async Task StopToolAsync(ToolViewModel t, bool unloadOnly)
    {
        var procs = t.Processes;
        if (!unloadOnly) t.BeginStop();
        try
        {
            var r = await ToolController.StopAsync(t.Definition, procs, unloadOnly);
            if (r.UnloadedModels.Count > 0) Log.Info(t.Name, "Unloaded " + string.Join(", ", r.UnloadedModels) + ".");
            else if (unloadOnly && r.Errors.Count == 0) Log.Info(t.Name, "No models were loaded.");
            foreach (string e in r.Errors) Log.Error(t.Name, e);
            if (r.Errors.Count > 0) t.Error = r.Errors[0];
        }
        catch (Exception ex)
        {
            // Never let a failed stop vanish silently or stop the other targets of "Free the GPU".
            CrashLog.Write($"Stopping {t.Name}", ex);
            t.Error = "Stop failed: " + ex.Message;
            Log.Error(t.Name, t.Error);
        }
        finally
        {
            t.EndStop();
            _monitor.RequestScan();
        }
    }

    private void AskRemove(ToolViewModel t)
    {
        ShowConfirm($"Remove {t.Name}?",
            "It is only removed from PCHUB's list. Nothing is uninstalled or deleted, and Rescan can add it back.",
            "Remove",
            () =>
            {
                var raw = ToolStore.LoadRaw();
                raw.RemoveAll(d => string.Equals(d.Id, t.Id, StringComparison.OrdinalIgnoreCase));
                ToolStore.SaveRaw(raw);
                ReloadTools();
                Log.Info("Tools", $"Removed {t.Name} from the list.");
                return Task.CompletedTask;
            });
    }

    // ---------- find apps (opt-in suggestions) ----------

    private bool _scanning;
    private string _scanStatus = "";

    /// <summary>Apps found on this PC that the user may add. Nothing is added until they tick and confirm.</summary>
    public ObservableCollection<SuggestionViewModel> Suggestions { get; } = new();
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool Scanning { get => _scanning; private set { if (Set(ref _scanning, value)) { Raise(nameof(ShowEmptyState)); Raise(nameof(ShowScanPanel)); } } }
    public string ScanStatus { get => _scanStatus; private set { if (Set(ref _scanStatus, value)) Raise(nameof(ShowScanPanel)); } }
    public bool ShowScanPanel => Scanning || HasSuggestions || ScanStatus.Length > 0;

    /// <summary>The "no apps yet" message shows only when there is nothing else to show.</summary>
    public bool ShowEmptyState => Tools.Count == 0 && !HasSuggestions && !Scanning;

    public ICommand AddSuggestionsCommand { get; private set; } = null!;
    public ICommand DismissSuggestionsCommand { get; private set; } = null!;

    private void SetUpSuggestions()
    {
        AddSuggestionsCommand = new RelayCommand(AddChosenSuggestions);
        DismissSuggestionsCommand = new RelayCommand(() =>
        {
            Suggestions.Clear();
            ScanStatus = "";
        });
        Suggestions.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasSuggestions));
            Raise(nameof(ShowScanPanel));
            Raise(nameof(ShowEmptyState));
        };
    }

    /// <summary>Looks for known AI apps (only when the user presses Find) and lists them as suggestions.</summary>
    private async Task RescanAsync()
    {
        if (Scanning) return;
        Scanning = true;
        ScanStatus = "Looking for AI apps on this PC…";
        Suggestions.Clear();
        try
        {
            var excludes = _settings.ScanExcludes.ToList();
            var found = await Task.Run(() => ToolDetector.Detect(excludes));
            var known = new HashSet<string>(ToolStore.LoadRaw().Select(d => d.FileName), StringComparer.OrdinalIgnoreCase);
            foreach (var f in found.Where(f => !known.Contains(f.FileName)))
            {
                Suggestions.Add(new SuggestionViewModel(f));
            }

            ScanStatus = Suggestions.Count switch
            {
                0 when found.Count > 0 => "Everything PCHUB recognises is already in your list.",
                0 => "No known AI apps found. Use Add app to add yours.",
                _ => "Found on this PC. Tick the ones you want PCHUB to manage:",
            };
        }
        catch (Exception ex)
        {
            CrashLog.Write("Find apps", ex);
            ScanStatus = "The search failed: " + ex.Message;
        }
        finally
        {
            Scanning = false;
        }
    }

    private void AddChosenSuggestions()
    {
        var chosen = Suggestions.Where(s => s.IsChecked).Select(s => s.Definition).ToList();
        if (chosen.Count == 0)
        {
            ScanStatus = "Tick at least one app, or press Not now.";
            return;
        }

        try
        {
            var raw = ToolStore.LoadRaw();
            var ids = new HashSet<string>(raw.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var f in chosen)
            {
                string id = f.Id;
                for (int n = 2; ids.Contains(id); n++) id = $"{f.Id}-{n}";
                f.Id = id;
                ids.Add(id);
                raw.Add(f);
            }

            ToolStore.SaveRaw(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ScanStatus = "Couldn't save your list: " + ex.Message;
            return;
        }

        Suggestions.Clear();
        ScanStatus = "";
        ReloadTools();
        Log.Info("Tools", "Added " + string.Join(", ", chosen.Select(c => c.Name)) + ".");
    }

    // ---------- add app ----------

    private void OpenAddDialog()
    {
        AddName = AddProgram = AddArgs = AddPort = AddUrl = "";
        AddHidden = false;
        AddError = null;
        AddOpen = true;
    }

    private void BrowseProgram()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program or launcher",
            Filter = "Programs and launchers (*.exe;*.bat;*.cmd;*.vbs)|*.exe;*.bat;*.cmd;*.vbs|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        AddProgram = dlg.FileName;
        if (string.IsNullOrWhiteSpace(AddName))
        {
            AddName = Path.GetFileNameWithoutExtension(dlg.FileName).Replace('_', ' ').Replace('-', ' ');
        }
    }

    private void SaveAdd()
    {
        string program = AddProgram.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(AddName)) { AddError = "Give it a name."; return; }
        if (!File.Exists(program)) { AddError = "Choose a program that exists."; return; }

        int? port = null;
        if (!string.IsNullOrWhiteSpace(AddPort))
        {
            if (!int.TryParse(AddPort.Trim(), out int p) || p is < 1 or > 65535) { AddError = "Port must be a number from 1 to 65535."; return; }
            port = p;
        }

        string folder = Path.GetDirectoryName(program)!;
        string ext = Path.GetExtension(program).ToLowerInvariant();
        var def = new ToolDefinition
        {
            Name = AddName.Trim(),
            WorkingDirectory = folder,
            Arguments = AddArgs.Trim(),
            Hidden = AddHidden,
            Port = port,
            Url = string.IsNullOrWhiteSpace(AddUrl) ? (port is int pp ? $"http://127.0.0.1:{pp}" : null) : AddUrl.Trim(),
        };

        if (ext == ".vbs")
        {
            def.FileName = "wscript.exe";
            def.Arguments = $"\"{program}\" {def.Arguments}".Trim();
        }
        else
        {
            def.FileName = program;
        }

        // A program sitting in a shared folder (Downloads, Desktop, a drive root) is matched by file only,
        // so PCHUB never claims unrelated programs that happen to live beside it.
        if (PathSafety.IsTooBroad(folder))
        {
            if (ext != ".exe")
            {
                AddError = "Launchers in shared folders like Downloads or Desktop can't be tracked. Move the app into its own folder first.";
                return;
            }

            def.MatchFiles.Add(program);
        }
        else
        {
            def.MatchPaths.Add(folder);
        }

        var raw = ToolStore.LoadRaw();
        string baseId = ToolDetector.Slug(def.Name);
        string id = baseId;
        for (int n = 2; raw.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)); n++) id = $"{baseId}-{n}";
        def.Id = id;
        raw.Add(def);
        ToolStore.SaveRaw(raw);
        AddOpen = false;
        ReloadTools();
        Log.Info("Tools", $"Added {def.Name}.");
    }

    // ---------- free the GPU ----------

    private Task AskFreeGpuAsync()
    {
        var targets = Tools.Where(t => t.Definition.IncludeInFreeGpu && t.Processes.Count > 0 && t.State != ToolState.Stopping).ToList();
        if (targets.Count == 0)
        {
            FreeStatus = "None of your apps are running, so there's nothing to free.";
            return Task.CompletedTask;
        }

        var close = targets.Where(t => !t.IsOllama).Select(t => $"{t.Name} ({t.VramText})").ToList();
        var lines = new List<string>();
        if (close.Count > 0) lines.Add("Close: " + string.Join(", ", close));
        if (targets.Any(t => t.IsOllama)) lines.Add("Unload all Ollama models (Ollama itself keeps running)");

        var others = GpuProcesses.Where(p => string.IsNullOrEmpty(p.Owner) && p.VramBytes > 512L * 1024 * 1024).Take(4).ToList();
        if (others.Count > 0)
        {
            lines.Add("");
            lines.Add("Not touched: " + string.Join(", ", others.Select(o => $"{o.Name} ({o.VramText})")));
        }

        if (close.Count > 0)
        {
            lines.Add("");
            lines.Add("Apps are asked to close first, then ended if they don't. Unsaved work in them may be lost.");
        }

        ShowConfirm("Free the GPU?", string.Join(Environment.NewLine, lines), "Free the GPU", () => FreeGpuAsync(targets));
        return Task.CompletedTask;
    }

    private async Task FreeGpuAsync(List<ToolViewModel> targets)
    {
        _freeing = true;
        CommandManager.InvalidateRequerySuggested();
        FreeStatus = "Freeing…";
        try
        {
            ulong? before = await Task.Run(_monitor.ReadUsedBytesNow);
            await Task.WhenAll(targets.Select(t => StopToolAsync(t, unloadOnly: t.IsOllama)));

            // Let the driver release memory, then read until it settles (max ~6 s).
            ulong? after = before;
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(500);
                ulong? now = await Task.Run(_monitor.ReadUsedBytesNow);
                if (now is null) break;
                bool settled = after is ulong a && Math.Abs((long)a - (long)now.Value) < 48L * 1024 * 1024 && i >= 2;
                after = now;
                if (settled) break;
            }

            if (before is ulong b && after is ulong f)
            {
                long freed = (long)b - (long)f;
                FreeStatus = freed > 0
                    ? $"Freed {Format.Bytes(freed)}  ({Format.Bytes(b)} → {Format.Bytes(f)})"
                    : $"VRAM now {Format.Bytes(f)}";
                Log.Info("Free the GPU", FreeStatus + ".");
            }
            else
            {
                FreeStatus = "Done.";
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Free the GPU", ex);
            FreeStatus = "Something went wrong: " + ex.Message;
            Log.Error("Free the GPU", FreeStatus);
        }
        finally
        {
            _freeing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------- confirm dialog ----------

    private readonly Queue<(string Title, string Text, string Button, Func<Task> Action)> _confirmQueue = new();

    /// <summary>
    /// Shows a yes/no question. If one is already on screen, the new one waits its turn (it never replaces
    /// the question the user is reading). Duplicates are dropped and at most 5 can wait, so a page or
    /// program can't flood the user with prompts.
    /// </summary>
    private void ShowConfirm(string title, string text, string button, Func<Task> action)
    {
        if (ConfirmOpen)
        {
            bool duplicate = (ConfirmTitle == title && ConfirmText == text) || _confirmQueue.Any(q => q.Title == title && q.Text == text);
            if (!duplicate && _confirmQueue.Count < 5) _confirmQueue.Enqueue((title, text, button, action));
            return;
        }

        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmButton = button;
        _confirmAction = action;
        ConfirmOpen = true;
    }

    private void CloseConfirm()
    {
        ConfirmOpen = false;
        _confirmAction = null;
        if (_confirmQueue.Count > 0)
        {
            var next = _confirmQueue.Dequeue();
            // Show the next one after this click has finished, so a double-click can't answer it by accident.
            _ui.InvokeAsync(() => ShowConfirm(next.Title, next.Text, next.Button, next.Action), DispatcherPriority.Background);
        }
    }

    private async Task RunConfirmAsync()
    {
        var action = _confirmAction;
        CloseConfirm();
        if (action is null) return;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Confirmed action", ex);
            Log.Error("PCHUB", ex.Message);
        }
    }

    // ---------- shell helpers ----------

    private void OpenToolLog(ToolViewModel t)
    {
        string log = AppPaths.ToolLog(t.Id);
        if (!File.Exists(log))
        {
            Log.Info(t.Name, "No log yet. Logs are written when PCHUB starts this app.");
            return;
        }

        // Many tools colour their console output; show a clean copy without the escape codes.
        string view = Path.Combine(AppPaths.Logs, t.Id + ".view.txt");
        try
        {
            using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 4 * 1024 * 1024) fs.Seek(-4 * 1024 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            string text = System.Text.RegularExpressions.Regex.Replace(reader.ReadToEnd(), @"\x1B\[[0-9;?]*[A-Za-z]", "");
            File.WriteAllText(view, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            view = log; // fall back to the raw file
        }

        ShellOpen("notepad.exe", $"\"{view}\"");
    }

    private void OpenToolFolder(ToolViewModel t)
    {
        string? dir = t.Definition.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            dir = t.Definition.MatchPaths.FirstOrDefault(Directory.Exists);
        }

        if (dir is null)
        {
            Log.Warn(t.Name, "Its folder no longer exists.");
            return;
        }

        ShellOpen(dir);
    }

    private void ShellOpen(string target, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("PCHUB", $"Couldn't open {target}: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        _monitor.SnapshotReady -= OnSnapshot;
        _driveTimer?.Stop();
    }
}
