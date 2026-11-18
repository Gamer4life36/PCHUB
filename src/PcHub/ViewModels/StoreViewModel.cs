using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using PcHub.Services;

namespace PcHub.ViewModels;

public sealed class StoreItemViewModel : ObservableObject
{
    private string _status = "";
    private double _progress;
    private bool _busy;
    private string? _downloadedPath;
    private CancellationTokenSource? _cts;

    public StoreItemViewModel(CatalogApp app) => App = app;

    public CatalogApp App { get; }
    public string Name => App.Name;
    public string Description => App.Description;
    public string CategoryText => App.Category.ToString();
    public bool IsNsfw => App.Rating == AppRating.Nsfw;
    public bool IsUnrated => App.Rating == AppRating.Unrated;
    public string RatingText => App.Rating switch { AppRating.Sfw => "SFW", AppRating.Nsfw => "NSFW 18+", _ => "Not rated" };
    public string RatingGroup => App.Rating switch
    {
        AppRating.Sfw => "1|SFW",
        AppRating.Nsfw => "2|NSFW (18+): no content filter",
        _ => "3|Not rated: PCHUB couldn't tell from the project's description",
    };

    /// <summary>Recommended apps first, then by GitHub stars.</summary>
    public long SortRank => (App.Recommended ? 1_000_000_000L : 0) + (App.Stars ?? 0);

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (App.Recommended) parts.Add("Recommended");
            if (App.Stars is int s) parts.Add($"★ {(s >= 1000 ? (s / 1000.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + "k" : s.ToString(System.Globalization.CultureInfo.CurrentCulture))}");
            if (App.Updated is DateTime u) parts.Add("updated " + u.ToLocalTime().ToString("MMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
            if (App.Repo is null) parts.Add("website download");
            return string.Join("  ·  ", parts);
        }
    }

    public bool CanDownload => App.Repo is not null && !_busy;
    public bool IsBusy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(CanDownload)); } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string? DownloadedPath
    {
        get => _downloadedPath;
        set
        {
            if (!Set(ref _downloadedPath, value)) return;
            Raise(nameof(HasDownloaded));
            Raise(nameof(CanCheckAgeGate));
        }
    }
    public bool HasDownloaded => _downloadedPath is not null;

    private string _ageGateText = "";
    private bool _ageGateWarn;
    private bool _ageGateChecking;

    /// <summary>Result of the Claude age-gate check for this download.</summary>
    public string AgeGateText { get => _ageGateText; set { if (Set(ref _ageGateText, value)) Raise(nameof(HasAgeGateText)); } }
    public bool HasAgeGateText => _ageGateText.Length > 0;

    /// <summary>True when Claude found no age gate (shown as a warning).</summary>
    public bool AgeGateWarn { get => _ageGateWarn; set => Set(ref _ageGateWarn, value); }
    public bool AgeGateChecking { get => _ageGateChecking; set { if (Set(ref _ageGateChecking, value)) Raise(nameof(CanCheckAgeGate)); } }
    public bool CanCheckAgeGate => HasDownloaded && !_ageGateChecking;

    public CancellationToken BeginWork()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        return _cts.Token;
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* work already finished */ }
    }

    public void EndWork() => IsBusy = false;
}

/// <summary>
/// "Get apps": a built-in list of popular local AI apps plus GitHub search, grouped SFW / NSFW (18+) /
/// Not rated. NSFW entries stay hidden until the user confirms they are 18 or older.
/// </summary>
public sealed class StoreViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ActivityLog _log;
    private readonly Action<string, string, string, Func<Task>> _confirm;
    private readonly string _gpu;
    private string _query = "";
    private string _category = "All";
    private string _status = "";
    private bool _searching;
    private CancellationTokenSource? _searchCts;

    public StoreViewModel(AppSettings settings, ActivityLog log, string gpuVendor, Action<string, string, string, Func<Task>> confirm)
    {
        _settings = settings;
        _log = log;
        _gpu = gpuVendor;
        _confirm = confirm;

        // Only apps that run on this system (e.g. NVIDIA-only video apps are left out on a Mac).
        foreach (var app in AppCatalog.Recommended.Where(a => a.OnThisOs)) Items.Add(new StoreItemViewModel(app));
        Items.CollectionChanged += (_, _) => RefreshViews();
        RefreshViews();

        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        CategoryCommand = new RelayCommand(p => { if (p is string c) Category = c; });
        DownloadCommand = new RelayCommand(p => { if (p is StoreItemViewModel { CanDownload: true } i) _ = PrepareDownloadAsync(i); });
        CancelDownloadCommand = new RelayCommand(p => (p as StoreItemViewModel)?.Cancel());
        WebsiteCommand = new RelayCommand(p => { if (p is StoreItemViewModel i) OpenUrl(i.App.Website); });
        ShowInFolderCommand = new RelayCommand(p => { if (p is StoreItemViewModel { DownloadedPath: { } path }) ShowInFolder(path); });
        CheckAgeGateCommand = new RelayCommand(p => { if (p is StoreItemViewModel { CanCheckAgeGate: true } i) _ = CheckAgeGateAsync(i); });
        SetClaudeKeyCommand = new RelayCommand(() => _ = SetKeyAsync());
        ChangeFolderCommand = new RelayCommand(() => _ = ChangeFolderAsync());
        OpenFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(DownloadFolder);
                OpenUrl(DownloadFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Status = "Couldn't open the download folder: " + ex.Message;
            }
        });
    }

    /// <summary>Shown on the Get apps page and in every download prompt.</summary>
    public const string ResponsibilityNotice =
        "You are responsible for everything you download. No matter which app, SFW or NSFW, it is up to you to make sure " +
        "the files are clean (free of malware) and legal to use where you live. Before using any download, have Claude scan it " +
        "(\"Scan with Claude\") and scan it with your antivirus. PCHUB and Claude's scan help, but they can't guarantee a file is safe or legal.";

    public string Notice => ResponsibilityNotice;

    public ObservableCollection<StoreItemViewModel> Items { get; } = new();
    /// <summary>Left column.</summary>
    public ObservableCollection<StoreItemViewModel> SfwView { get; } = new();

    /// <summary>Right column (empty until the 18+ opt-in).</summary>
    public ObservableCollection<StoreItemViewModel> NsfwView { get; } = new();

    /// <summary>Bottom box: online results PCHUB couldn't classify.</summary>
    public ObservableCollection<StoreItemViewModel> UnratedView { get; } = new();

    /// <summary>Rebuilds the three boxes from Items (filtered, recommended first, then by stars).</summary>
    private void RefreshViews()
    {
        Fill(SfwView, AppRating.Sfw);
        Fill(NsfwView, AppRating.Nsfw);
        Fill(UnratedView, AppRating.Unrated);
        Raise(nameof(HasUnrated));
        Raise(nameof(NsfwHidden));
        Raise(nameof(NoUnrated));
        Raise(nameof(ShowNsfwList));
    }

    private void Fill(ObservableCollection<StoreItemViewModel> target, AppRating rating)
    {
        var wanted = Items.Where(i => i.App.Rating == rating && Matches(i)).OrderByDescending(i => i.SortRank).ToList();
        if (wanted.SequenceEqual(target)) return;
        target.Clear();
        foreach (var i in wanted) target.Add(i);
    }

    public bool HasUnrated => UnratedView.Count > 0;
    public bool NoUnrated => UnratedView.Count == 0;
    public bool ShowNsfwList => _settings.ShowAdultApps;
    public bool NsfwHidden => !_settings.ShowAdultApps;

    public ICommand SearchCommand { get; }
    public ICommand CategoryCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand WebsiteCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ChangeFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CheckAgeGateCommand { get; }
    public ICommand SetClaudeKeyCommand { get; }

    // ---------- Claude age-gate check (opt-in, user's own API key) ----------

    public bool HasClaudeKey => ClaudeKey.HasKey(_settings);
    public string ClaudeKeyText => HasClaudeKey ? "API key saved" : "No API key";

    /// <summary>Turning this on explains what is sent and what it costs, and asks for a key if there isn't one.</summary>
    public bool ClaudeAgeCheck
    {
        get => _settings.ClaudeAgeCheck;
        set
        {
            if (value == _settings.ClaudeAgeCheck) return;
            if (!value)
            {
                _settings.ClaudeAgeCheck = false;
                Raise();
                return;
            }

            Raise(); // stays off until confirmed
            _confirm("Scan downloads with Claude?",
                "After each download, PCHUB sends the app's README and the parts of its files that matter for safety (scripts that download or run things, " +
                "hidden code, password or wallet access) and for age-gating to Anthropic's Claude. " +
                "Claude reports safety red flags, legal red flags, and whether the app has its own age gate." + Environment.NewLine + Environment.NewLine +
                "• Uses your own Anthropic API key (stored encrypted on this PC)." + Environment.NewLine +
                "• Costs roughly $0.05–$0.15 per check, billed to your Anthropic account." + Environment.NewLine +
                "• Red flags or a missing age gate show as a warning and are noted in History." + Environment.NewLine +
                "• The scan reads excerpts and can't guarantee a file is safe or legal; you stay responsible for what you use.",
                "Turn on",
                () =>
                {
                    return TurnOnAsync();
                });

            async Task TurnOnAsync()
            {
                if (!HasClaudeKey && !await SetKeyAsync()) return;
                _settings.ClaudeAgeCheck = true;
                Raise(nameof(ClaudeAgeCheck));
            }
        }
    }

    /// <summary>Asks for the key in a small password box; returns true if one is saved afterwards.</summary>
    private async Task<bool> SetKeyAsync()
    {
        if (Ui.Dialogs is null) return false;
        string? key = await Ui.Dialogs.AskApiKeyAsync();
        if (key is null) return false;
        if (key.Length == 0)
        {
            ClaudeKey.Save(_settings, null);
            _settings.ClaudeAgeCheck = false;
            Raise(nameof(ClaudeAgeCheck));
        }
        else if (!ClaudeKey.Save(_settings, key))
        {
            Status = "The system couldn't store the key securely for this account, so it wasn't saved.";
            return false;
        }

        Raise(nameof(HasClaudeKey));
        Raise(nameof(ClaudeKeyText));
        return HasClaudeKey;
    }

    private async Task CheckAgeGateAsync(StoreItemViewModel item)
    {
        // Off the UI thread: on macOS reading the Keychain can wait on the user's "Allow" prompt.
        string? key = await Task.Run(() => ClaudeKey.Get(_settings));
        if (key is null || item.DownloadedPath is null)
        {
            item.AgeGateText = "Claude check skipped: no API key set.";
            return;
        }

        item.AgeGateChecking = true;
        item.AgeGateWarn = false;
        item.AgeGateText = "Claude is checking the files (safety, legal, age gate)…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var r = await AgeGateScanner.ScanAsync(item.DownloadedPath, item.App.Repo, key, timeout.Token);
            var lines = new List<string>();
            if (r.SafetyConcerns is { Count: > 0 } safety) lines.Add("⚠ Safety red flags: " + string.Join("; ", safety) + ".");
            if (r.LegalConcerns is { Count: > 0 } legal) lines.Add("⚠ Legal red flags: " + string.Join("; ", legal) + ".");
            if (!r.HasConcerns) lines.Add("No safety or legal red flags in what Claude read.");

            string found = r.HasAgeGate ? "Age gate found."
                : r.Verdict == "unclear" ? "Couldn't tell if there's an age gate."
                : "No age gate found.";
            string extras = string.Join(", ", new[]
            {
                r.HasNsfwFilter ? "has an NSFW filter" : null,
                r.HasContentWarning ? "shows a content warning" : null,
            }.Where(x => x is not null));
            lines.Add(found + (extras.Length > 0 ? " It " + extras + "." : ""));
            lines.Add(r.Summary);
            lines.Add($"Checked: {r.Scope}. This check reads excerpts only and can't guarantee the files are safe or legal.");

            item.AgeGateWarn = r.HasConcerns || !r.HasAgeGate;
            item.AgeGateText = "Claude: " + string.Join(Environment.NewLine, lines);
            if (r.HasConcerns)
            {
                _log.Error("Claude check", $"{item.Name}: red flags found. " + string.Join(" ", lines.Take(2)));
            }
            else if (!r.HasAgeGate)
            {
                _log.Warn("Claude check", $"{item.Name}: no red flags; {found}");
            }
            else
            {
                _log.Info("Claude check", $"{item.Name}: no red flags; age gate found.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            item.AgeGateWarn = true;
            item.AgeGateText = "Claude check failed: " + ex.Message;
            _log.Error("Age-gate check", $"{item.Name}: {ex.Message}");
        }
        finally
        {
            item.AgeGateChecking = false;
        }
    }

    public string Query { get => _query; set => Set(ref _query, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Searching { get => _searching; private set => Set(ref _searching, value); }

    public string Category
    {
        get => _category;
        set
        {
            if (!Set(ref _category, value)) return;
            Raise(nameof(IsAll)); Raise(nameof(IsImage)); Raise(nameof(IsVideo)); Raise(nameof(IsChat));
            RefreshViews();
        }
    }

    public bool IsAll => _category == "All";
    public bool IsImage => _category == "Image";
    public bool IsVideo => _category == "Video";
    public bool IsChat => _category == "Chat";

    public string DownloadFolder => string.IsNullOrWhiteSpace(_settings.DownloadFolder) ? AppCatalog.DefaultDownloadFolder : _settings.DownloadFolder!;

    /// <summary>Turning this on asks for 18+ confirmation first.</summary>
    public bool ShowAdult
    {
        get => _settings.ShowAdultApps;
        set
        {
            if (value == _settings.ShowAdultApps) return;
            if (!value)
            {
                _settings.ShowAdultApps = false;
                Raise();
                RefreshViews();
                return;
            }

            Raise(); // snap the checkbox back until confirmed
            _confirm("Show NSFW (18+) apps?",
                "These apps have no content filter and are often used to make adult images, video or chat." +
                Environment.NewLine + Environment.NewLine + "Only continue if you are 18 or older. You are responsible for making sure anything you download and make with these apps is legal where you live.",
                "I'm 18 or older",
                () =>
                {
                    _settings.ShowAdultApps = true;
                    Raise(nameof(ShowAdult));
                    if (Status.Contains("hidden", StringComparison.Ordinal)) Status = "";
                    RefreshViews();
                    return Task.CompletedTask;
                });
        }
    }

    private bool Matches(StoreItemViewModel i)
    {
        if (i.App.Rating == AppRating.Nsfw && !_settings.ShowAdultApps) return false;
        return _category == "All" || i.App.Category.ToString() == _category;
    }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var mine = new CancellationTokenSource();
        _searchCts = mine;
        var ct = mine.Token;
        var categories = _category == "All" ? Enum.GetValues<AppCategory>() : [Enum.Parse<AppCategory>(_category)];

        Searching = true;
        Status = "Searching GitHub…";
        try
        {
            // Drop previous online results (keep the built-in list), then add fresh ones.
            foreach (var old in Items.Where(i => !i.App.Recommended && !i.IsBusy && !i.HasDownloaded).ToList()) Items.Remove(old);
            var known = Items.Where(i => i.App.Repo is not null).Select(i => i.App.Repo!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0, hidden = 0;
            bool all = categories.Length > 1;
            foreach (var cat in categories)
            {
                for (int t = 0; t < AppCatalog.TopicCount(cat, all); t++)
                {
                    var results = await AppCatalog.SearchAsync(cat, Query, t, ct);
                    foreach (var r in results)
                    {
                        if (r.Repo is null || !known.Add(r.Repo)) continue;
                        Items.Add(new StoreItemViewModel(r));
                        added++;
                        if (r.Rating == AppRating.Nsfw && !_settings.ShowAdultApps) hidden++;
                    }
                }
            }

            Status = added == 0 ? "No new apps found. Try different words."
                : $"Found {added} on GitHub." + (hidden > 0 ? $" {hidden} NSFW result(s) hidden (turn on NSFW to see them)." : "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer search replaced this one; it owns the status line.
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException
                                   or OperationCanceledException or NotSupportedException)
        {
            Status = ex is HttpRequestException ? "Couldn't reach GitHub. Check your internet connection."
                : ex is OperationCanceledException ? "GitHub didn't answer in time. Try again."
                : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_searchCts, mine)) Searching = false;
        }
    }

    /// <summary>Finds the exact file first, then asks the user (file, size, source, folder) before downloading.</summary>
    private async Task PrepareDownloadAsync(StoreItemViewModel item)
    {
        var ct = item.BeginWork();
        item.Status = "Checking the latest version…";
        DownloadChoice choice;
        try
        {
            choice = await AppCatalog.ResolveDownloadAsync(item.App.Repo!, _gpu, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or NotSupportedException)
        {
            item.Status = ex is HttpRequestException ? "Couldn't reach GitHub."
                : ex is OperationCanceledException ? "GitHub didn't answer in time. Try again."
                : ex.Message;
            item.EndWork();
            return;
        }

        item.Status = "";
        item.EndWork();
        string size = choice.SizeBytes is long b ? Format.Bytes(b) : "size shown by GitHub when it starts";
        _confirm($"Download {item.Name}?",
            $"File: {choice.FileName}{Environment.NewLine}" +
            $"What: {choice.What}{Environment.NewLine}" +
            $"Size: {size}{Environment.NewLine}" +
            $"From: github.com/{item.App.Repo}{Environment.NewLine}" +
            $"Checksum: {(choice.Sha256 is null ? "none published, so the file can't be verified" : "GitHub's SHA-256, checked after download")}{Environment.NewLine}" +
            $"To: {DownloadFolder}{Environment.NewLine}{Environment.NewLine}" +
            "PCHUB only saves the file. It won't run or install anything." + Environment.NewLine + Environment.NewLine + ResponsibilityNotice,
            "Download",
            () => DownloadAsync(item, choice));
    }

    private async Task DownloadAsync(StoreItemViewModel item, DownloadChoice choice)
    {
        var ct = item.BeginWork();
        item.Progress = 0;
        item.Status = "Starting download…";
        var progress = new Progress<(long Done, long? Total)>(p =>
        {
            item.Progress = p.Total is long t && t > 0 ? (double)p.Done / t : 0;
            item.Status = p.Total is long tt ? $"{Format.Bytes(p.Done)} of {Format.Bytes(tt)}" : Format.Bytes(p.Done);
        });

        try
        {
            string path = await Task.Run(() => AppCatalog.DownloadAsync(choice, DownloadFolder, progress, ct), ct);
            item.DownloadedPath = path;
            if (_settings.ClaudeAgeCheck) _ = CheckAgeGateAsync(item);
            item.Status = "Saved: " + Path.GetFileName(path) + (_settings.ClaudeAgeCheck ? "" : ". Before using it, scan it with Claude and your antivirus.");
            _log.Info("Get apps", $"Downloaded {item.Name} to {path}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Status = "Download cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException
                                   or ArgumentException or NotSupportedException or System.Security.SecurityException or OperationCanceledException)
        {
            item.Status = "Download failed: " + ex.Message;
            _log.Error("Get apps", $"Downloading {item.Name} failed: {ex.Message}");
        }
        finally
        {
            item.EndWork();
        }
    }

    private async Task ChangeFolderAsync()
    {
        if (Ui.Dialogs is null) return;
        string? folder = await Ui.Dialogs.PickFolderAsync("Where should PCHUB save downloads?", DownloadFolder);
        if (folder is null) return;
        _settings.DownloadFolder = folder;
        Raise(nameof(DownloadFolder));
    }

    private void ShowInFolder(string path)
    {
        try
        {
            PcHub.Platform.Os.Current.RevealInFolder(path);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Error("Get apps", "Couldn't open the folder: " + ex.Message);
        }
    }

    /// <summary>Only called from a button the user pressed.</summary>
    private void OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log.Error("Get apps", $"Couldn't open {target}: {ex.Message}");
        }
    }
}