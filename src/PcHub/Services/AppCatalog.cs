using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PcHub.Services;

public enum AppCategory
{
    Image,
    Video,
    Chat,
}

/// <summary>
/// Sfw: has a content filter or ships with safe defaults.
/// Nsfw: no content filter and commonly used for adult content (shown only after the 18+ opt-in).
/// Unrated: an online result PCHUB can't classify.
/// </summary>
public enum AppRating
{
    Sfw,
    Nsfw,
    Unrated,
}

public sealed record CatalogApp(
    string Name,
    AppCategory Category,
    AppRating Rating,
    string Description,
    string? Repo,
    string Website,
    int? Stars = null,
    DateTime? Updated = null,
    bool Recommended = false);

/// <summary>The exact file a download will fetch, shown to the user before anything is downloaded.</summary>
public sealed record DownloadChoice(string Url, string FileName, long? SizeBytes, string? Sha256, string What);

/// <summary>
/// The "Get apps" catalog: a built-in list of well-known local AI apps plus live GitHub search.
/// Only talks to api.github.com / github.com, and only when the user asks.
/// </summary>
public static partial class AppCatalog
{
    private static readonly HttpClient Api = CreateApiClient();

    /// <summary>Well-known apps. Ratings describe the app's own content filtering, not any one model.</summary>
    public static IReadOnlyList<CatalogApp> Recommended { get; } =
    [
        // Image
        new("ComfyUI", AppCategory.Image, AppRating.Nsfw, "Node-based image (and video) generation. Very flexible, no content filter.",
            "comfyanonymous/ComfyUI", "https://github.com/comfyanonymous/ComfyUI", Recommended: true),
        new("Stability Matrix", AppCategory.Image, AppRating.Nsfw, "One-click installer and manager for ComfyUI, Forge, Fooocus and more.",
            "LykosAI/StabilityMatrix", "https://github.com/LykosAI/StabilityMatrix", Recommended: true),
        new("Fooocus", AppCategory.Image, AppRating.Nsfw, "Simple image generator: type a prompt and go. No content filter.",
            "lllyasviel/Fooocus", "https://github.com/lllyasviel/Fooocus", Recommended: true),
        new("Forge (SD WebUI)", AppCategory.Image, AppRating.Nsfw, "Fast Stable Diffusion / Flux web UI. No content filter.",
            "lllyasviel/stable-diffusion-webui-forge", "https://github.com/lllyasviel/stable-diffusion-webui-forge", Recommended: true),
        new("SwarmUI", AppCategory.Image, AppRating.Nsfw, "Friendly image studio built on ComfyUI. No content filter.",
            "mcmonkeyprojects/SwarmUI", "https://github.com/mcmonkeyprojects/SwarmUI", Recommended: true),
        new("InvokeAI", AppCategory.Image, AppRating.Sfw, "Polished creative studio with canvas tools and an optional NSFW checker.",
            "invoke-ai/launcher", "https://www.invoke.com/downloads", Recommended: true),

        // Video
        new("FramePack", AppCategory.Video, AppRating.Nsfw, "Image-to-video that runs on 6 GB GPUs. No content filter.",
            "lllyasviel/FramePack", "https://github.com/lllyasviel/FramePack", Recommended: true),
        new("Wan2GP", AppCategory.Video, AppRating.Nsfw, "Wan / Hunyuan / LTX video generation for low-VRAM GPUs. No content filter.",
            "deepbeepmeep/Wan2GP", "https://github.com/deepbeepmeep/Wan2GP", Recommended: true),

        // Chat
        new("Ollama", AppCategory.Chat, AppRating.Sfw, "Run chat models locally with one command. Default models are safety-tuned.",
            "ollama/ollama", "https://ollama.com", Recommended: true),
        new("LM Studio", AppCategory.Chat, AppRating.Sfw, "Desktop app to download and chat with local models.",
            null, "https://lmstudio.ai", Recommended: true),
        new("Jan", AppCategory.Chat, AppRating.Sfw, "Offline ChatGPT-style desktop app.",
            "janhq/jan", "https://jan.ai", Recommended: true),
        new("GPT4All", AppCategory.Chat, AppRating.Sfw, "Private local chat app that runs on ordinary PCs.",
            "nomic-ai/gpt4all", "https://www.nomic.ai/gpt4all", Recommended: true),
        new("AnythingLLM", AppCategory.Chat, AppRating.Sfw, "Chat with your documents, fully local.",
            "Mintplex-Labs/anything-llm", "https://anythingllm.com", Recommended: true),
        new("KoboldCpp", AppCategory.Chat, AppRating.Nsfw, "Single-exe chat and story writer. No content filter.",
            "LostRuins/koboldcpp", "https://github.com/LostRuins/koboldcpp", Recommended: true),
        new("SillyTavern", AppCategory.Chat, AppRating.Nsfw, "Character chat and role-play front end. No content filter.",
            "SillyTavern/SillyTavern", "https://sillytavernai.com", Recommended: true),
        new("Text Generation WebUI", AppCategory.Chat, AppRating.Nsfw, "Full-featured chat web UI with portable builds. No content filter.",
            "oobabooga/text-generation-webui", "https://github.com/oobabooga/text-generation-webui", Recommended: true),
    ];

    /// <summary>GitHub topic tags per category; the first is used when searching all categories at once.</summary>
    private static readonly Dictionary<AppCategory, string[]> CategoryTopics = new()
    {
        [AppCategory.Image] = ["text-to-image", "stable-diffusion-webui", "image-generation"],
        [AppCategory.Video] = ["text-to-video", "image-to-video", "video-generation"],
        [AppCategory.Chat] = ["llm-ui", "local-llm", "chatgpt-app"],
    };

    /// <summary>How many topic searches to run (GitHub allows ~10 unauthenticated searches a minute).</summary>
    public static int TopicCount(AppCategory c, bool allCategories) => allCategories ? 1 : CategoryTopics[c].Length;

    private static readonly string[] NsfwWords = ["nsfw", "uncensored", "adult", "erotic", "porn", "hentai", "roleplay", "role-play", "waifu", "unfiltered", "unrestricted"];
    private static readonly string[] SfwWords = ["safety checker", "safety-checker", "content filter", "nsfw filter", "family friendly", "family-friendly", "sfw"];

    /// <summary>"no content filter", "without safety checker", "disable the nsfw filter" mean the opposite of a filter.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(no|without|disable[sd]?|remove[sd]?|bypass(es|ed)?|turn(s|ed)?\s+off|free\s+of)\s+(the\s+|any\s+|all\s+)?(nsfw\s+|content\s+|safety\s+)?(filter|filters|filtering|checker|censorship|restrictions?)\b")]
    private static partial System.Text.RegularExpressions.Regex NegatedFilter();

    /// <summary>GitHub "owner/name": letters, digits, '.', '-', '_' only.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}/[A-Za-z0-9._-]{1,100}$")]
    private static partial System.Text.RegularExpressions.Regex RepoName();

    public static bool IsValidRepo(string? repo) => repo is not null && RepoName().IsMatch(repo) && !repo.Contains("..", StringComparison.Ordinal);

    /// <summary>True for a real GitHub rate limit (403/429 with no requests left), not any 403.</summary>
    private static bool IsRateLimited(HttpResponseMessage resp) =>
        resp.StatusCode == HttpStatusCode.TooManyRequests ||
        (resp.StatusCode == HttpStatusCode.Forbidden &&
         resp.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0");

    /// <summary>Searches GitHub for apps in a category (optionally narrowed by the user's words). Popular, maintained repos first.</summary>
    public static async Task<List<CatalogApp>> SearchAsync(AppCategory category, string? words, int topicIndex, CancellationToken ct)
    {
        string topic = CategoryTopics[category][Math.Clamp(topicIndex, 0, CategoryTopics[category].Length - 1)];
        string cleanWords = new string((words ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.').ToArray()).Trim();
        if (cleanWords.Length > 80) cleanWords = cleanWords[..80];
        string q = $"{cleanWords} topic:{topic} stars:>100 archived:false fork:false".Trim();
        string url = "https://api.github.com/search/repositories?sort=stars&order=desc&per_page=20&q=" + Uri.EscapeDataString(q);
        using var resp = await Api.GetAsync(url, ct).ConfigureAwait(false);
        if (IsRateLimited(resp))
        {
            throw new InvalidOperationException("GitHub's search limit was reached (about 10 searches a minute). Try again shortly.");
        }

        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadFromJsonAsync(GitHubJson.Default.GhSearchResult, ct).ConfigureAwait(false);
        var list = new List<CatalogApp>();
        foreach (var r in data?.Items ?? [])
        {
            if (!IsValidRepo(r.FullName)) continue;
            string text = (r.Description ?? "") + " " + string.Join(' ', r.Topics ?? []) + " " + r.Name;
            // Only ever link to the repo's own github.com page, whatever the API returned.
            list.Add(new CatalogApp(r.Name ?? r.FullName!, category, Classify(text), Trim(r.Description ?? "No description."),
                r.FullName, $"https://github.com/{r.FullName}", r.Stars, r.PushedAt));
        }

        return list;
    }

    /// <summary>Keyword classification for online results; anything unclear stays Unrated.</summary>
    public static AppRating Classify(string text)
    {
        string t = text.ToLowerInvariant();
        if (NegatedFilter().IsMatch(t)) return AppRating.Nsfw; // "no content filter" = unfiltered
        if (NsfwWords.Any(w => t.Contains(w, StringComparison.Ordinal))) return AppRating.Nsfw;
        if (SfwWords.Any(w => t.Contains(w, StringComparison.Ordinal))) return AppRating.Sfw;
        return AppRating.Unrated;
    }

    /// <summary>
    /// Picks the Windows download for this PC (CPU architecture and GPU maker) from the latest release, or
    /// from the most recent release that has one; falls back to the source code zip. Nothing is downloaded here.
    /// </summary>
    /// <param name="gpu">"nvidia", "amd", "intel" or "none".</param>
    public static async Task<DownloadChoice> ResolveDownloadAsync(string repo, string gpu, CancellationToken ct)
    {
        if (!IsValidRepo(repo)) throw new InvalidOperationException("That project name isn't valid.");
        var latest = await GetReleasesAsync($"https://api.github.com/repos/{repo}/releases/latest", single: true, ct).ConfigureAwait(false);
        var pick = Pick(latest, gpu);
        if (pick is null)
        {
            var recent = await GetReleasesAsync($"https://api.github.com/repos/{repo}/releases?per_page=10", single: false, ct).ConfigureAwait(false);
            pick = Pick(recent, gpu);
        }

        if (pick is var (rel, best) && best is not null)
        {
            string? sha = best.Digest is { } d && d.Length == 71 && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d[7..].All(char.IsAsciiHexDigit) ? d[7..] : null;
            return new DownloadChoice(best.DownloadUrl!, SafeFileName(best.Name!), best.Size, sha, $"Windows release {rel.TagName}".Trim());
        }

        string name = repo.Replace('/', '-') + "-source.zip";
        return new DownloadChoice($"https://api.github.com/repos/{repo}/zipball", name, null, null,
            "Source code (no Windows installer published; see the project's install guide)");
    }

    private static async Task<List<GhRelease>> GetReleasesAsync(string url, bool single, CancellationToken ct)
    {
        using var resp = await Api.GetAsync(url, ct).ConfigureAwait(false);
        if (IsRateLimited(resp))
        {
            throw new InvalidOperationException("GitHub's request limit was reached. Try again in a minute.");
        }

        if (!resp.IsSuccessStatusCode) return [];
        if (single)
        {
            var one = await resp.Content.ReadFromJsonAsync(GitHubJson.Default.GhRelease, ct).ConfigureAwait(false);
            return one is null ? [] : [one];
        }

        return await resp.Content.ReadFromJsonAsync(GitHubJson.Default.ListGhRelease, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>The best-scoring Windows asset of the first release (newest first) that has one.</summary>
    private static (GhRelease Release, GhAsset Asset)? Pick(List<GhRelease> releases, string gpu)
    {
        foreach (var rel in releases.Where(r => !r.Draft && !r.Prerelease || releases.Count == 1))
        {
            var best = (rel.Assets ?? [])
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl) && IsGitHubHost(a.DownloadUrl!))
                .Select(a => (Asset: a, Score: Score(a.Name!, gpu)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Asset.Size)
                .Select(x => x.Asset)
                .FirstOrDefault();
            if (best is not null) return (rel, best);
        }

        return null;
    }

    /// <summary>Higher is better; 0 means "not a Windows download". gpu is "nvidia", "amd", "intel" or "none".</summary>
    private static int Score(string name, string gpu)
    {
        string n = name.ToLowerInvariant();
        // Whole-word OS tokens (so "Studio" or "Macro" in a name isn't mistaken for macOS/iOS) and non-installer files.
        string[] wrongOsTokens = ["linux", "mac", "macos", "darwin", "osx", "android", "ios", "apple", "ubuntu", "debian"];
        string[] wrongEndings = [".dmg", ".appimage", ".deb", ".rpm", ".tar.gz", ".tgz", ".sha256", ".sig", ".asc", ".txt", ".json", ".yml", ".yaml", ".blockmap", ".whl", ".pkg"];
        var tokens = n.Split(['-', '_', '.', ' ', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => wrongOsTokens.Contains(t)) || wrongEndings.Any(e => n.EndsWith(e, StringComparison.Ordinal))) return 0;
        bool isArm = System.Text.RegularExpressions.Regex.IsMatch(n, @"(arm64|aarch64|(^|[-_.])arm([-_.]|$))");
        if (isArm != (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)) return 0;

        int score = n switch
        {
            _ when n.EndsWith(".exe") => 50,
            _ when n.EndsWith(".msi") => 45,
            _ when n.EndsWith(".zip") => 40,
            _ when n.EndsWith(".7z") => 40,
            _ when n.EndsWith(".bat") => 35,
            _ => 0,
        };
        if (score == 0) return 0;
        if (n.Contains("win")) score += 30;
        if (n.Contains("x64") || n.Contains("amd64") || n.Contains("x86_64")) score += 10;
        if (n.Contains("setup") || n.Contains("installer")) score += 30;
        else if (n.Contains("install") || n.Contains("portable")) score += 5;

        // Match the GPU maker's build; other makers' builds are pushed down, CPU-only builds slightly.
        string n2 = n.Replace("amd64", "", StringComparison.Ordinal).Replace("nocuda", "cpu", StringComparison.Ordinal);
        string? vendor = n2.Contains("nvidia") || n2.Contains("cuda") || System.Text.RegularExpressions.Regex.IsMatch(n2, @"(^|[-_.])cu1\d")
            ? "nvidia"
            : n2.Contains("amd") || n2.Contains("rocm") ? "amd"
            : n2.Contains("intel") || n2.Contains("xpu") ? "intel"
            : null;
        if (vendor is not null) score += vendor == gpu ? 15 : -25;
        if (n2.Contains("cpu")) score += gpu == "none" ? 10 : -5;
        if (n.Contains("mlx")) score -= 25; // Apple MLX builds
        if (n.Contains("oldpc")) score -= 5;
        if (n.Contains("debug") || n.Contains("symbols") || n.Contains("pdb")) score -= 40;
        return Math.Max(score, 0);
    }
    private const long MaxDownloadBytes = 25L * 1024 * 1024 * 1024; // 25 GB hard cap
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Downloads to "&lt;file&gt;.part", checks the size and the SHA-256 if GitHub published one, marks it as coming
    /// from the internet (so Windows SmartScreen still checks it), then gives it its final name. Never runs the file.
    /// Redirects are followed by hand (max 5) and every hop must be a GitHub download host.
    /// </summary>
    public static async Task<string> DownloadAsync(DownloadChoice choice, string folder, IProgress<(long Done, long? Total)> progress, CancellationToken ct)
    {
        if (!IsGitHubHost(choice.Url)) throw new InvalidOperationException("Only GitHub downloads are allowed.");
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);

        using var download = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        download.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCHUB", "1.0"));

        HttpResponseMessage? resp = null;
        var url = new Uri(choice.Url);
        try
        {
            for (int hop = 0; ; hop++)
            {
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(StallTimeout);
                resp = await download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
                {
                    var next = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                    resp.Dispose();
                    resp = null;
                    if (hop >= 5) throw new InvalidOperationException("The download redirected too many times and was stopped.");
                    if (!IsGitHubHost(next.AbsoluteUri))
                    {
                        throw new InvalidOperationException($"The download tried to go to an unexpected site ({next.Host}) and was stopped.");
                    }

                    url = next;
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                break;
            }

            long? total = resp.Content.Headers.ContentLength ?? choice.SizeBytes;
            if (total > MaxDownloadBytes) throw new IOException("This file is larger than PCHUB's 25 GB download limit.");
            if (choice.SizeBytes is long expectedSize && resp.Content.Headers.ContentLength is long announced && announced != expectedSize)
            {
                throw new IOException("GitHub reported a different file size than expected, so the download was stopped.");
            }

            if (total is long need) CheckFreeSpace(folder, need);

            string target = UniquePath(Path.Combine(folder, SafeFileName(choice.FileName)));
            string part = target + ".part";
            try
            {
                using var sha = SHA256.Create();
                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                {
                    var buf = new byte[1 << 20];
                    long done = 0, lastReport = 0;
                    while (true)
                    {
                        // A read that makes no progress for a minute is treated as a stalled connection.
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(StallTimeout);
                        int n;
                        try
                        {
                            n = await src.ReadAsync(buf, readCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException("The download stalled (no data for a minute). Try again.");
                        }

                        if (n == 0) break;
                        done += n;
                        if (done > MaxDownloadBytes) throw new IOException("This file is larger than PCHUB's 25 GB download limit.");
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        if (done - lastReport > 4 << 20)
                        {
                            lastReport = done;
                            progress.Report((done, total));
                        }
                    }

                    sha.TransformFinalBlock([], 0, 0);
                    progress.Report((done, total));
                    if (choice.SizeBytes is long expected && expected != done)
                    {
                        throw new IOException($"The download was incomplete ({done} of {expected} bytes).");
                    }
                }

                if (choice.Sha256 is { } want && !string.Equals(Convert.ToHexString(sha.Hash!), want, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The downloaded file doesn't match GitHub's checksum, so it was deleted.");
                }

                // Mark before the rename: the mark travels with the file, so it is never visible unmarked.
                MarkFromInternet(part, url.AbsoluteUri);
                for (int i = 0; ; i++)
                {
                    try
                    {
                        File.Move(part, target);
                        break;
                    }
                    catch (IOException) when (i < 5 && File.Exists(target))
                    {
                        target = UniquePath(target); // a file with that name appeared meanwhile
                    }
                }

                return target;
            }
            catch
            {
                try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort cleanup */ }
                throw;
            }
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private static void CheckFreeSpace(string folder, long need)
    {
        try
        {
            string? root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return; // network folders: skip the check
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < need + 200L * 1024 * 1024)
            {
                throw new IOException($"Not enough free space on {drive.Name} ({need / 1048576} MB needed).");
            }
        }
        catch (ArgumentException)
        {
            // Unusual path: let the write itself report problems.
        }
    }

    /// <summary>Documents\PCHUB Downloads, or Downloads / LocalAppData if Documents is unavailable on a locked-down PC.</summary>
    public static string DefaultDownloadFolder
    {
        get
        {
            foreach (string baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            })
            {
                if (!string.IsNullOrWhiteSpace(baseDir) && Path.IsPathFullyQualified(baseDir)) return Path.Combine(baseDir, "PCHUB Downloads");
            }

            return Path.Combine(AppContext.BaseDirectory, "PCHUB Downloads");
        }
    }

    /// <summary>True when the file got the "downloaded from the internet" mark (fails on FAT32/exFAT drives).</summary>
    private static bool MarkFromInternet(string file, string url)
    {
        try
        {
            File.WriteAllText(file + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://github.com/\r\nHostUrl={url}\r\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Only GitHub's own hosts and its release-download hosts (not user-content hosts like raw/gist).</summary>
    private static bool IsGitHubHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort &&
        u.Host is "github.com" or "api.github.com" or "codeload.github.com"
            or "objects.githubusercontent.com" or "release-assets.githubusercontent.com";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A plain, safe Windows file name: no path parts, device names, trailing dots/spaces; extension kept.</summary>
    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name.Replace('/', '\\'));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        string ext = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ');
        if (ext.Length > 12) { stem = name; ext = ""; }
        if (stem.Length == 0 || stem.All(c => c == '.') || ReservedNames.Contains(stem)) stem = "download-" + stem.Trim('.');
        if (stem.Length > 140) stem = stem[..140].TrimEnd('.', ' ');
        return stem + ext;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".part")) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !File.Exists(p + ".part")) return p;
        }
    }

    private static string Trim(string s) => s.Length > 220 ? s[..217] + "…" : s;

    private static HttpClient CreateApiClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // A generic client name only: no user details are sent.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCHUB", "1.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }
}

public sealed class GhRepo
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("stargazers_count")] public int Stars { get; set; }
    [JsonPropertyName("pushed_at")] public DateTime? PushedAt { get; set; }
    [JsonPropertyName("topics")] public List<string>? Topics { get; set; }
}

public sealed class GhSearchResult
{
    [JsonPropertyName("items")] public List<GhRepo>? Items { get; set; }
}

public sealed class GhAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

public sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GhAsset>? Assets { get; set; }
}

[JsonSerializable(typeof(GhSearchResult))]
[JsonSerializable(typeof(GhRelease))]
[JsonSerializable(typeof(List<GhRelease>))]
internal sealed partial class GitHubJson : JsonSerializerContext;
