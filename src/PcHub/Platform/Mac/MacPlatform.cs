using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using PcHub.Services;

namespace PcHub.Platform.Mac;

/// <summary>
/// GPU on macOS. Apple Silicon shares memory between CPU and GPU, so there is no separate VRAM: PCHUB shows
/// the memory the GPU driver has in use and its utilisation, read from the driver's own statistics
/// (`ioreg`, no admin rights needed). macOS doesn't report GPU memory per app, so per-app numbers are RAM.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacGpuMonitor : IGpuMonitor
{
    [GeneratedRegex("\"(Device Utilization %|In use system memory|inUseVidMemoryBytes|vramUsedBytes)\"\\s*=\\s*(\\d+)")]
    private static partial Regex StatLine();

    private readonly ulong _total;

    public MacGpuMonitor()
    {
        string cpu = MacNative.SysctlString("machdep.cpu.brand_string") ?? "Mac";
        Name = cpu.StartsWith("Apple", StringComparison.Ordinal) ? cpu + " GPU" : "Mac GPU";
        _total = MacNative.SysctlUInt64("hw.memsize") ?? 0;
        bool appleSilicon = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
        Detail = appleSilicon ? "Apple Silicon · shared memory" : "Mac · GPU load and memory";
        Note = "macOS doesn't share GPU temperature or power without admin rights, so those tiles are off. Per-app numbers below are RAM.";
    }

    public string Name { get; }
    public string Detail { get; }
    public string? Note { get; }
    public bool HasSensors => false;
    public string MemoryLabel => "GPU memory";
    public bool PerProcessIsGpuMemory => false;
    public ulong TotalBytes => _total;

    public GpuSample? ReadFast() => null;

    public GpuScanReading ReadScan(IReadOnlyDictionary<int, ProcInfo> processes)
    {
        var (used, load, error) = ReadDriverStats();
        GpuSample? sample = used is null ? null : new GpuSample(used.Value, _total, load, null, null, null, null, null, null);

        // RAM of the bigger processes (macOS has no per-app GPU memory figure).
        var perProcess = new Dictionary<int, GpuProcUsage>();
        foreach (var p in processes.Values)
        {
            ulong rss = MacNative.ResidentBytes(p.Pid);
            if (rss >= 256UL * 1024 * 1024) perProcess[p.Pid] = new GpuProcUsage((long)rss, 0);
        }

        return new GpuScanReading(perProcess, sample, error);
    }

    public ulong? UsedBytesNow() => ReadDriverStats().Used;

    /// <summary>Runs `ioreg` once and sums the accelerator statistics.</summary>
    private static (ulong? Used, int Load, string? Error) ReadDriverStats()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/ioreg", "-r -d 1 -w 0 -c IOAccelerator")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (null, 0, "Couldn't read GPU statistics.");
            // Bounded: a hung ioreg must never stall sampling or shutdown.
            var read = p.StandardOutput.ReadToEndAsync();
            if (!read.Wait(3000))
            {
                try { p.Kill(); }
                catch (InvalidOperationException) { /* already exited */ }
                return (null, 0, "GPU statistics took too long to read.");
            }

            string text = read.Result;
            p.WaitForExit(1000);

            ulong used = 0;
            int load = 0;
            bool any = false;
            foreach (Match m in StatLine().Matches(text))
            {
                if (!ulong.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v)) continue;
                any = true;
                if (m.Groups[1].Value == "Device Utilization %") load = Math.Max(load, (int)Math.Min(100, v));
                else used += v;
            }

            return any ? (used, load, null) : (null, 0, "This Mac's GPU driver doesn't publish usage statistics.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (null, 0, "Couldn't read GPU statistics: " + ex.Message);
        }
    }

    public void Dispose()
    {
        // Nothing held open.
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacPlatform : IPlatform
{
    private readonly object _scanGate = new();
    private Dictionary<int, ProcInfo> _cache = new();

    public string OsName => "macOS";
    public bool IsWindows => false;
    public bool IsMac => true;

    // APFS volumes are case-insensitive by default; matching that way is the safe choice for "is this inside that folder".
    public StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    public IGpuMonitor CreateGpuMonitor(AppSettings settings) => new MacGpuMonitor();

    // ---------------- processes ----------------

    public unsafe IReadOnlyDictionary<int, ProcInfo> ScanProcesses()
    {
        lock (_scanGate)
        {
            int count = MacNative.ProcListAllPids(null, 0);
            if (count <= 0) return _cache;
            int[] pids = new int[count + 64];
            fixed (int* buf = pids)
            {
                count = MacNative.ProcListAllPids(buf, pids.Length * sizeof(int));
            }

            var next = new Dictionary<int, ProcInfo>(Math.Max(16, count));
            for (int i = 0; i < count; i++)
            {
                int pid = pids[i];
                if (pid <= 1 || !MacNative.TryGetBsdInfo(pid, out var bsd)) continue;
                long created = MacNative.StartFileTime(bsd);
                int ppid = (int)bsd.PPid;
                if (_cache.TryGetValue(pid, out var known) && known.CreateTime == created)
                {
                    next[pid] = known with { ParentPid = ppid };
                    continue;
                }

                string? path = MacNative.PidPath(pid);
                string name = path is not null ? Path.GetFileName(path) : MacNative.CommName(bsd);
                next[pid] = new ProcInfo(pid, ppid, name, path, created);
            }

            _cache = next;
            return next;
        }
    }

    /// <summary>
    /// Asks each process to quit (SIGTERM), waits, then forces the rest (SIGKILL). Before every signal the
    /// process's start time is re-checked, so a PID that now belongs to another program is never signalled.
    /// </summary>
    public Task<EndResult> EndProcessesAsync(IReadOnlyList<ProcInfo> procs, int graceSeconds) => Task.Run(async () =>
    {
        var errors = new List<string>();
        var self = SelfAndAncestors();
        uint me = MacNative.GetEuid();
        // Only this user's own processes: even if PCHUB were run as root, nobody else's programs are touched.
        var targets = procs.Where(p => p.CreateTime != 0 && !IsProtected(p, self)
                                       && MacNative.TryGetBsdInfo(p.Pid, out var b) && b.Uid == me)
            .OrderByDescending(p => p.CreateTime).ToList();

        foreach (var p in targets.Where(IsSameProcess)) MacNative.Kill(p.Pid, MacNative.SigTerm);

        if (graceSeconds > 0)
        {
            var deadline = DateTime.UtcNow.AddSeconds(graceSeconds);
            while (DateTime.UtcNow < deadline && targets.Any(IsSameProcess)) await Task.Delay(250).ConfigureAwait(false);
        }

        int closed = 0, killed = 0;
        foreach (var p in targets)
        {
            if (!IsSameProcess(p))
            {
                closed++;
                continue;
            }

            if (MacNative.Kill(p.Pid, MacNative.SigKill) == 0) killed++;
            else if (!IsSameProcess(p)) closed++; // it exited between the check and the signal
            else errors.Add($"Couldn't end {p.Name} ({p.Pid}). It may belong to another user or the system.");
        }

        return new EndResult(closed, killed, errors);
    });

    /// <summary>Still the same, still-running process (a zombie has already exited and only awaits its parent).</summary>
    private static bool IsSameProcess(ProcInfo p) =>
        MacNative.TryGetBsdInfo(p.Pid, out var bsd) && bsd.Status != MacNative.SZomb && MacNative.StartFileTime(bsd) == p.CreateTime;

    private HashSet<int> SelfAndAncestors()
    {
        var set = new HashSet<int> { Environment.ProcessId };
        var all = ScanProcesses();
        int pid = Environment.ProcessId;
        for (int i = 0; i < 32 && all.TryGetValue(pid, out var p) && p.ParentPid > 1 && set.Add(p.ParentPid); i++) pid = p.ParentPid;
        return set;
    }

    // ---------------- starting ----------------

    public ProcessStartInfo BuildStart(ToolDefinition t, string workDir, string? logPath)
    {
        string file = t.FileName;

        // App bundles are opened through Launch Services, like double-clicking them in Finder.
        if (file.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(file))
        {
            var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, WorkingDirectory = workDir };
            open.ArgumentList.Add("-a");
            open.ArgumentList.Add(file);
            if (!string.IsNullOrWhiteSpace(t.Arguments))
            {
                open.ArgumentList.Add("--args");
                foreach (string a in SplitArgs(t.Arguments)) open.ArgumentList.Add(a);
            }

            return open;
        }

        bool script = file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
        string command = (script ? "/bin/bash " : "") + Quote(file) + (string.IsNullOrWhiteSpace(t.Arguments) ? "" : " " + t.Arguments);
        if (logPath is not null)
        {
            // The shell owns the log file, so closing PCHUB never breaks the tool's output.
            command = $"exec {command} >> {Quote(logPath)} 2>&1";
        }
        else
        {
            command = "exec " + command;
        }

        var psi = new ProcessStartInfo("/bin/zsh") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        // An app opened from Finder only gets /usr/bin:/bin:/usr/sbin:/sbin; AI tools need Homebrew's python, git, uv.
        string path = psi.Environment.TryGetValue("PATH", out string? p) && !string.IsNullOrEmpty(p) ? p : "/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (string extra in new[] { "/usr/local/bin", "/opt/homebrew/bin" })
        {
            if (!path.Split(':').Contains(extra)) path = extra + ":" + path;
        }

        psi.Environment["PATH"] = path;
        return psi;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static IEnumerable<string> SplitArgs(string args)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in args)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) yield return current.ToString();
    }

    // ---------------- app detection ----------------

    private static readonly (string Bundle, string Name, string Kind, int? Port)[] KnownApps =
    [
        ("Ollama.app", "Ollama", "Ollama", 11434),
        ("LM Studio.app", "LM Studio", "App", null),
        ("Jan.app", "Jan", "App", null),
        ("ComfyUI.app", "ComfyUI Desktop", "App", null),
        ("Invoke Community Edition.app", "Invoke", "App", null),
        ("Pinokio.app", "Pinokio", "App", null),
        ("AnythingLLM.app", "AnythingLLM", "App", null),
        ("Msty.app", "Msty", "App", null),
        ("GPT4All.app", "GPT4All", "App", null),
        ("DiffusionBee.app", "DiffusionBee", "App", null),
        ("Draw Things.app", "Draw Things", "App", null),
        ("Stability Matrix.app", "Stability Matrix", "App", null),
    ];

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void DetectFixedApps(List<ToolDefinition> found)
    {
        foreach (string apps in new[] { "/Applications", Path.Combine(Home, "Applications") })
        {
            foreach (var (bundle, name, kind, port) in KnownApps)
            {
                string path = Path.Combine(apps, bundle);
                if (!Directory.Exists(path)) continue;
                var t = new ToolDefinition
                {
                    Id = ToolDetector.Slug(name), Name = name, Kind = kind, FileName = path, WorkingDirectory = apps,
                    MatchPaths = [path], Port = port,
                };
                if (name == "LM Studio" && Directory.Exists(Path.Combine(Home, ".lmstudio"))) t.MatchPaths.Add(Path.Combine(Home, ".lmstudio"));
                found.Add(t);
            }

            // GPT4All installs into its own folder with the app inside.
            string gpt = Path.Combine(apps, "gpt4all", "bin", "gpt4all.app");
            if (Directory.Exists(gpt))
            {
                found.Add(new ToolDefinition
                {
                    Id = "gpt4all", Name = "GPT4All", FileName = gpt, WorkingDirectory = Path.Combine(apps, "gpt4all"),
                    MatchPaths = [Path.Combine(apps, "gpt4all")],
                });
            }
        }

        // Ollama from Homebrew (the command-line server, no app bundle).
        foreach (string brew in new[] { "/opt/homebrew", "/usr/local" })
        {
            string bin = Path.Combine(brew, "bin", "ollama");
            string cellar = Path.Combine(brew, "Cellar", "ollama");
            if (!File.Exists(bin) || !Directory.Exists(cellar) || found.Any(f => f.IsOllama)) continue;
            found.Add(new ToolDefinition
            {
                Id = "ollama", Name = "Ollama", Kind = "Ollama", FileName = bin, Arguments = "serve",
                WorkingDirectory = Home, Hidden = true, MatchPaths = [cellar], Port = 11434,
            });
        }
    }

    public IEnumerable<string> ScanRoots()
    {
        var roots = new List<string> { Home };
        foreach (string sub in new[] { "Documents", "Desktop", "Downloads", "Developer", "Projects", "AI" })
        {
            string p = Path.Combine(Home, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }

        try
        {
            // External drives, but not the system's own volume mounts.
            roots.AddRange(Directory.GetDirectories("/Volumes").Where(v => !File.Exists(Path.Combine(v, "System", "Library", "CoreServices", "SystemVersion.plist"))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No external drives readable.
        }

        return roots;
    }

    public string? VenvPython(string folder)
    {
        foreach (string v in new[] { "venv", ".venv" })
        {
            foreach (string py in new[] { "python", "python3" })
            {
                string p = Path.Combine(folder, v, "bin", py);
                if (File.Exists(p)) return p;
            }
        }

        return null;
    }

    public string? FindExecutable(string folder, string stem)
    {
        string p = Path.Combine(folder, "bin", stem);
        return File.Exists(p) ? p : null;
    }

    // ---------------- safety rules ----------------

    private static readonly string[] SystemRoots = ["/System", "/usr/libexec", "/usr/sbin", "/sbin", "/bin", "/usr/bin", "/Library/Apple"];

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launchd", "kernel_task", "WindowServer", "loginwindow", "Finder", "Dock", "SystemUIServer",
        "coreservicesd", "mds", "mds_stores", "distnoted", "cfprefsd", "securityd", "PCHUB",
    };

    private static readonly HashSet<string> NeverInherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "Safari", "Google Chrome", "firefox", "Arc", "Brave Browser", "Microsoft Edge", "Opera",
        "Terminal", "iTerm2", "Finder", "TextEdit", "Code", "Electron Helper",
    };

    private static readonly string[] Shells = ["/bin/zsh", "/bin/bash", "/bin/sh", "/usr/bin/env", "/usr/bin/open"];

    public bool IsTooBroad(string folder)
    {
        string f = PathSafety.Norm(folder);
        if (f == "/" || f.Length <= 1) return true;
        if (SystemRoots.Any(r => f.Equals(r, StringComparison.OrdinalIgnoreCase) || PathSafety.IsUnder(f, r))) return true;
        string[] exact =
        [
            Home, Path.Combine(Home, "Downloads"), Path.Combine(Home, "Desktop"), Path.Combine(Home, "Documents"),
            Path.Combine(Home, "Library"), Path.Combine(Home, "Applications"), "/Applications", "/Users", "/Library",
            "/usr", "/usr/local", "/usr/local/bin", "/opt", "/opt/homebrew", "/opt/homebrew/bin", "/opt/homebrew/Cellar",
            "/tmp", "/private", "/private/tmp", "/private/var", "/var", "/etc", "/Volumes", "/Users/Shared",
            "/Applications/Utilities", Path.GetTempPath(),
        ];
        if (exact.Any(e => PathSafety.Norm(e).Equals(f, StringComparison.OrdinalIgnoreCase))) return true;

        // The root of any mounted drive (/Volumes/Name), including "Macintosh HD", which links back to "/".
        string parent = Path.GetDirectoryName(f) ?? "";
        return parent.Equals("/Volumes", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors) =>
        p.Pid <= 1 || selfAndAncestors.Contains(p.Pid) || ProtectedNames.Contains(p.Name) ||
        (p.Path is { } path && SystemRoots.Any(r => PathSafety.IsUnder(path, r)));

    public bool CanInherit(ProcInfo p)
    {
        if (NeverInherited.Contains(p.Name) || ProtectedNames.Contains(p.Name)) return false;
        if (p.Path is { } path)
        {
            if (path.Contains("/Safari.app/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Google Chrome.app/", StringComparison.OrdinalIgnoreCase)) return false;
            if (SystemRoots.Any(r => PathSafety.IsUnder(path, r))) return Shells.Contains(path, StringComparer.Ordinal);
        }

        return true;
    }

    // ---------------- files and shell ----------------

    /// <summary>Adds the quarantine mark, so macOS Gatekeeper checks the file when it is first opened.</summary>
    public unsafe bool MarkFromInternet(string file, string sourceUrl)
    {
        string value = $"0081;{DateTimeOffset.UtcNow.ToUnixTimeSeconds():x};PCHUB;";
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        fixed (byte* b = bytes)
        {
            return MacNative.SetXattr(file, "com.apple.quarantine", b, (nuint)bytes.Length, 0, 0) == 0;
        }
    }

    public void OpenWithShell(string target) => RunOpen(target);
    public void RevealInFolder(string file) => RunOpen("-R", file);
    public void OpenTextFile(string file) => RunOpen("-t", file);

    private static void RunOpen(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    // ---------------- secrets (Keychain) ----------------

    private static readonly byte[] Service = Encoding.UTF8.GetBytes("PCHUB Claude API key");
    private static readonly byte[] Account = Encoding.UTF8.GetBytes("PCHUB");

    public unsafe string? LoadSecret(AppSettings settings)
    {
        fixed (byte* s = Service)
        fixed (byte* a = Account)
        {
            uint len = 0;
            void* data = null;
            nint item = 0;
            int status = MacNative.SecKeychainFindGenericPassword(0, (uint)Service.Length, s, (uint)Account.Length, a, &len, &data, &item);
            if (status != 0) return null;
            try
            {
                return Encoding.UTF8.GetString((byte*)data, (int)len);
            }
            finally
            {
                _ = MacNative.SecKeychainItemFreeContent(0, data);
                if (item != 0) MacNative.CFRelease(item);
            }
        }
    }

    private const int ErrSecItemNotFound = MacNative.ErrSecItemNotFound;

    public unsafe bool HasSecret(AppSettings settings)
    {
        fixed (byte* s = Service)
        fixed (byte* a = Account)
        {
            nint item = 0;
            // No length/data pointers: finds the item without reading the secret, so macOS doesn't ask.
            int status = MacNative.SecKeychainFindGenericPassword(0, (uint)Service.Length, s, (uint)Account.Length, a, null, null, &item);
            if (item != 0) MacNative.CFRelease(item);
            return status == 0;
        }
    }

    public unsafe bool SaveSecret(AppSettings settings, string? secret)
    {
        fixed (byte* s = Service)
        fixed (byte* a = Account)
        {
            nint item = 0;
            int found = MacNative.SecKeychainFindGenericPassword(0, (uint)Service.Length, s, (uint)Account.Length, a, null, null, &item);
            try
            {
                if (found != 0 && found != ErrSecItemNotFound) return false; // locked or denied: don't guess

                if (string.IsNullOrWhiteSpace(secret))
                {
                    return found != 0 || MacNative.SecKeychainItemDelete(item) == 0;
                }

                byte[] pw = Encoding.UTF8.GetBytes(secret.Trim());
                fixed (byte* p = pw)
                {
                    if (found == 0) return MacNative.SecKeychainItemModifyAttributesAndData(item, 0, (uint)pw.Length, p) == 0;
                    return MacNative.SecKeychainAddGenericPassword(0, (uint)Service.Length, s, (uint)Account.Length, a, (uint)pw.Length, p, null) == 0;
                }
            }
            finally
            {
                if (item != 0) MacNative.CFRelease(item);
            }
        }
    }

    // ---------------- drives ----------------

    public List<DriveStatus> ReadDrives()
    {
        var list = new List<DriveStatus>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                string mount = d.RootDirectory.FullName.TrimEnd('/');
                bool root = mount.Length == 0;
                bool external = mount.StartsWith("/Volumes/", StringComparison.Ordinal);
                if (!(root || external) || !d.IsReady || d.TotalSize <= 0) continue;
                string label = root ? "Macintosh HD" : Path.GetFileName(mount);
                list.Add(new DriveStatus(root ? "/" : label, root ? "System" : "External", DriveKind.Unknown, d.TotalSize, d.AvailableFreeSpace, null, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable volume: skip.
            }
        }

        return list;
    }

    public bool CanOptimizeDrives => false;
    public Process? StartOptimize(string drive) => null;
    public (string Stage, int Percent)? ReadOptimizeProgress(string drive) => null;
    public string OptimizeLastLine(string drive) => "";
    public void OpenDriveTool() => RunOpen("-b", "com.apple.DiskUtility");
}
