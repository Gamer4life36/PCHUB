using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using PcHub.Platform.Windows;
using PcHub.Services;

namespace PcHub.Platform.Linux;

/// <summary>
/// GPU on Linux. NVIDIA cards are read through the driver's NVML library (the same full sensors as on Windows,
/// plus VRAM per app). AMD cards are read from the amdgpu driver's files in /sys (VRAM, load, temperature,
/// power). Other GPUs show their name only. No root needed for any of it.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxGpuMonitor : IGpuMonitor
{
    private readonly NvmlGpu? _nvml;
    private readonly string? _amdDevice; // /sys/class/drm/cardN/device
    private readonly string? _amdHwmon;  // .../hwmon/hwmonM

    public LinuxGpuMonitor()
    {
        try
        {
            Nvml.RegisterResolver();
            var nv = NvmlGpu.Open(null);
            if (nv.Available)
            {
                _nvml = nv;
                Name = nv.Name;
                Detail = $"NVIDIA driver {nv.Driver} · full sensors";
                HasSensors = true;
                TotalBytes = nv.Read()?.TotalBytes ?? 0;
                return;
            }

            nv.Dispose();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // No NVIDIA driver: try the other makers below.
        }

        (string? card, string vendor) = FindCard();
        if (card is not null && vendor == "0x1002" && LinuxNative.ReadSysNumber(Path.Combine(card, "mem_info_vram_total")) is { } total)
        {
            _amdDevice = card;
            _amdHwmon = FirstDir(Path.Combine(card, "hwmon"));
            TotalBytes = total;
            Name = ReadText(Path.Combine(card, "product_name")) ?? "AMD Radeon GPU";
            HasSensors = _amdHwmon is not null && File.Exists(Path.Combine(_amdHwmon, "temp1_input"));
            Detail = "amdgpu driver · VRAM, load" + (HasSensors ? ", temperature, power" : "");
            Note = "Per-app numbers below are RAM: the amdgpu driver doesn't report VRAM per app.";
            return;
        }

        Name = vendor switch
        {
            "0x8086" => "Intel GPU",
            "0x1002" => "AMD GPU",
            "0x10de" => "NVIDIA GPU",
            _ => "No supported GPU found",
        };
        Detail = "Sensors off";
        Note = vendor == "0x10de"
            ? "Install NVIDIA's own driver (not nouveau) to see VRAM, load and temperatures. Per-app numbers below are RAM."
            : "This GPU's Linux driver doesn't share usage figures. Per-app numbers below are RAM.";
    }

    public string Name { get; }
    public string Detail { get; }
    public string? Note { get; }
    public bool HasSensors { get; }
    public string MemoryLabel => "VRAM";
    public bool PerProcessIsGpuMemory => _nvml is not null;
    public ulong TotalBytes { get; }

    public GpuSample? ReadFast()
    {
        if (_nvml is not null) return _nvml.Read();
        if (_amdDevice is null) return null;

        ulong used = LinuxNative.ReadSysNumber(Path.Combine(_amdDevice, "mem_info_vram_used")) ?? 0;
        int load = (int)Math.Min(100, LinuxNative.ReadSysNumber(Path.Combine(_amdDevice, "gpu_busy_percent")) ?? 0);
        int? temp = null;
        double? power = null, limit = null;
        if (_amdHwmon is not null)
        {
            if (LinuxNative.ReadSysNumber(Path.Combine(_amdHwmon, "temp1_input")) is { } milli) temp = (int)(milli / 1000);
            // power1_average on older kernels, power1_input on newer ones; both in microwatts.
            if ((LinuxNative.ReadSysNumber(Path.Combine(_amdHwmon, "power1_average")) ?? LinuxNative.ReadSysNumber(Path.Combine(_amdHwmon, "power1_input"))) is { } uw) power = uw / 1_000_000.0;
            if (LinuxNative.ReadSysNumber(Path.Combine(_amdHwmon, "power1_cap")) is { } cap) limit = cap / 1_000_000.0;
        }

        return new GpuSample(used, TotalBytes, load, temp, power, limit, null, null, null);
    }

    public GpuScanReading ReadScan(IReadOnlyDictionary<int, ProcInfo> processes)
    {
        var perProcess = new Dictionary<int, GpuProcUsage>();
        if (_nvml is not null)
        {
            if (_nvml.ReadProcesses() is { } vram)
            {
                foreach (var (pid, bytes) in vram) perProcess[pid] = new GpuProcUsage((long)Math.Min(bytes, long.MaxValue), 0);
                return new GpuScanReading(perProcess, null, null);
            }

            return new GpuScanReading(perProcess, null, "This NVIDIA driver is too old to report VRAM per app (needs 510 or newer).");
        }

        // RAM of the bigger processes (no per-app GPU memory figure on this GPU).
        foreach (var p in processes.Values)
        {
            ulong rss = LinuxNative.ResidentBytes(p.Pid);
            if (rss >= 256UL * 1024 * 1024) perProcess[p.Pid] = new GpuProcUsage((long)rss, 0);
        }

        return new GpuScanReading(perProcess, null, null);
    }

    public ulong? UsedBytesNow() => ReadFast()?.UsedBytes;

    public void Dispose() => _nvml?.Dispose();

    /// <summary>The first GPU with a real display driver: AMD/NVIDIA (discrete) before Intel (integrated).</summary>
    private static (string? Card, string Vendor) FindCard()
    {
        string? best = null;
        string bestVendor = "";
        try
        {
            foreach (string card in Directory.GetDirectories("/sys/class/drm", "card*").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(card).Contains('-')) continue; // connectors like card0-HDMI-A-1
                string dev = Path.Combine(card, "device");
                string vendor = ReadText(Path.Combine(dev, "vendor")) ?? "";
                if (best is null || (bestVendor == "0x8086" && vendor is "0x1002" or "0x10de"))
                {
                    best = dev;
                    bestVendor = vendor;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No DRM devices visible (container, server without GPU).
        }

        return (best, bestVendor);
    }

    private static string? FirstDir(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir).Order(StringComparer.Ordinal).FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } s ? s : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxPlatform : IPlatform
{
    private readonly object _scanGate = new();
    private Dictionary<int, ProcInfo> _cache = new();

    public string OsName => "Linux";
    public bool IsWindows => false;
    public bool IsMac => false;
    public bool IsLinux => true;

    // Linux file systems are case-sensitive.
    public StringComparison PathComparison => StringComparison.Ordinal;

    public IGpuMonitor CreateGpuMonitor(AppSettings settings) => new LinuxGpuMonitor();

    // ---------------- processes ----------------

    public IReadOnlyDictionary<int, ProcInfo> ScanProcesses()
    {
        lock (_scanGate)
        {
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories("/proc");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return _cache;
            }

            var next = new Dictionary<int, ProcInfo>(Math.Max(16, dirs.Length));
            foreach (string dir in dirs)
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 1) continue;
                if (!LinuxNative.TryReadStat(pid, out var st)) continue;
                if (pid == 2 || st.ParentPid == 2) continue; // kernel threads
                long created = LinuxNative.StartFileTime(st.StartTicks);
                if (_cache.TryGetValue(pid, out var known) && known.CreateTime == created)
                {
                    next[pid] = known with { ParentPid = st.ParentPid };
                    continue;
                }

                string? path = LinuxNative.ReadLink($"/proc/{pid}/exe");
                string name = path is not null ? Path.GetFileName(path) : st.Comm;
                // Interpreters run from a shared system folder; their working folder tells which app they serve.
                string? cwd = name.StartsWith("python", StringComparison.Ordinal) ? LinuxNative.ReadLink($"/proc/{pid}/cwd") : null;
                next[pid] = new ProcInfo(pid, st.ParentPid, name, path, created, cwd);
            }

            _cache = next;
            return next;
        }
    }

    /// <summary>
    /// Asks each process to quit (SIGTERM), waits, then forces the rest (SIGKILL). Before every signal the
    /// process's start time is re-checked, so a PID that now belongs to another program is never signalled.
    /// Only the current user's own processes are touched.
    /// </summary>
    public Task<EndResult> EndProcessesAsync(IReadOnlyList<ProcInfo> procs, int graceSeconds) => Task.Run(async () =>
    {
        var errors = new List<string>();
        var self = SelfAndAncestors();
        uint me = LinuxNative.GetEuid();
        var targets = new List<ProcInfo>();
        foreach (var p in procs.Where(p => p.CreateTime != 0 && !IsProtected(p, self)).OrderByDescending(p => p.CreateTime))
        {
            if (LinuxNative.OwnerUid(p.Pid) == me) targets.Add(p);
            else if (IsSameProcess(p)) errors.Add($"{p.Name} ({p.Pid}) runs as another user (for example a system service), so PCHUB leaves it alone.");
        }

        foreach (var p in targets.Where(IsSameProcess)) LinuxNative.Kill(p.Pid, LinuxNative.SigTerm);

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

            if (LinuxNative.Kill(p.Pid, LinuxNative.SigKill) == 0) killed++;
            else if (!IsSameProcess(p)) closed++; // it exited between the check and the signal
            else errors.Add($"Couldn't end {p.Name} ({p.Pid}).");
        }

        return new EndResult(closed, killed, errors);
    });

    /// <summary>Still the same, still-running process (zombies and dying processes have already exited).</summary>
    private static bool IsSameProcess(ProcInfo p) =>
        LinuxNative.TryReadStat(p.Pid, out var st) && st.State is not ('Z' or 'X' or 'x') && LinuxNative.StartFileTime(st.StartTicks) == p.CreateTime;

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
        bool script = file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
        string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        string command = (script ? shell + " " : "") + Quote(file) + (string.IsNullOrWhiteSpace(t.Arguments) ? "" : " " + t.Arguments);
        command = logPath is not null
            ? $"exec {command} >> {Quote(logPath)} 2>&1" // the shell owns the log, so closing PCHUB never breaks it
            : "exec " + command;

        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        // A desktop launcher may start PCHUB with a short PATH; AI tools often live in ~/.local/bin (pip, uv, pipx).
        string home = Home;
        string path = psi.Environment.TryGetValue("PATH", out string? p) && !string.IsNullOrEmpty(p) ? p : "/usr/local/bin:/usr/bin:/bin";
        foreach (string extra in new[] { "/usr/local/bin", Path.Combine(home, ".local", "bin") })
        {
            if (!path.Split(':').Contains(extra)) path = extra + ":" + path;
        }

        psi.Environment["PATH"] = path;
        return psi;
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // ---------------- app detection ----------------

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>AppImage file names (lower case) of common AI apps, and the name to show.</summary>
    private static readonly (string Fragment, string Name, string Kind)[] KnownAppImages =
    [
        ("lm-studio", "LM Studio", "App"),
        ("lmstudio", "LM Studio", "App"),
        ("jan", "Jan", "App"),
        ("gpt4all", "GPT4All", "App"),
        ("pinokio", "Pinokio", "App"),
        ("anythingllm", "AnythingLLM", "App"),
        ("msty", "Msty", "App"),
        ("comfyui", "ComfyUI Desktop", "App"),
        ("stabilitymatrix", "Stability Matrix", "App"),
        ("stability-matrix", "Stability Matrix", "App"),
        ("invoke", "Invoke", "App"),
    ];

    public void DetectFixedApps(List<ToolDefinition> found)
    {
        // Ollama's installer puts the server in /usr/local/bin (often also run as a system service).
        foreach (string bin in new[] { "/usr/local/bin/ollama", "/usr/bin/ollama", Path.Combine(Home, ".local", "bin", "ollama") })
        {
            if (!File.Exists(bin) || found.Any(f => f.IsOllama)) continue;
            found.Add(new ToolDefinition
            {
                Id = "ollama", Name = "Ollama", Kind = "Ollama", FileName = bin, Arguments = "serve",
                WorkingDirectory = Home, Hidden = true, MatchFiles = [bin], Port = 11434,
            });
        }

        // AppImages in the usual places. Matched by their exact file (the folders are shared).
        foreach (string dir in new[] { Path.Combine(Home, "Applications"), Path.Combine(Home, "AppImages"), Path.Combine(Home, ".local", "bin"), Path.Combine(Home, "Downloads"), Path.Combine(Home, "Desktop"), "/opt" })
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir, "*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false })
                    .Where(f => f.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                string lower = Path.GetFileName(file).ToLowerInvariant();
                var match = KnownAppImages.FirstOrDefault(k => lower.StartsWith(k.Fragment, StringComparison.Ordinal));
                if (match.Name is null) continue;
                found.Add(new ToolDefinition
                {
                    Id = ToolDetector.Slug(match.Name), Name = match.Name, Kind = match.Kind, FileName = file, WorkingDirectory = dir,
                    MatchFiles = [file],
                });
            }
        }

        // GPT4All's own installer uses ~/gpt4all with the program in bin/chat.
        string gpt = Path.Combine(Home, "gpt4all");
        string chat = Path.Combine(gpt, "bin", "chat");
        if (File.Exists(chat))
        {
            found.Add(new ToolDefinition { Id = "gpt4all", Name = "GPT4All", FileName = chat, WorkingDirectory = gpt, MatchPaths = [gpt] });
        }
    }

    public IEnumerable<string> ScanRoots()
    {
        var roots = new List<string> { Home };
        foreach (string sub in new[] { "Documents", "Desktop", "Downloads", "Projects", "AI", "ai", "git", "src", "code" })
        {
            string p = Path.Combine(Home, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }

        // Other drives: /mnt/x, /media/<user>/x, /run/media/<user>/x.
        string user = Environment.UserName;
        foreach (string parent in new[] { "/mnt", Path.Combine("/media", user), Path.Combine("/run/media", user) })
        {
            try
            {
                if (Directory.Exists(parent)) roots.AddRange(Directory.GetDirectories(parent));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // not readable
            }
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

    /// <summary>System folders: never an app's folder, and their programs are never ended (except interpreters PCHUB started).</summary>
    private static readonly string[] SystemRoots =
        ["/usr/bin", "/usr/sbin", "/bin", "/sbin", "/usr/lib", "/lib", "/lib64", "/usr/lib64", "/usr/libexec", "/boot", "/proc", "/sys", "/dev", "/etc"];

    /// <summary>Daemons whose programs live in system folders; ending them would break the desktop.</summary>
    private static readonly string[] DaemonRoots = ["/usr/lib/systemd", "/lib/systemd", "/usr/sbin", "/sbin", "/usr/libexec"];

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.Ordinal)
    {
        "systemd", "init", "Xorg", "Xwayland", "gnome-shell", "plasmashell", "kwin_x11", "kwin_wayland", "mutter",
        "gdm", "gdm3", "sddm", "lightdm", "dbus-daemon", "dbus-broker", "pipewire", "wireplumber", "pulseaudio",
        "NetworkManager", "sshd", "login", "polkitd", "gnome-session-binary", "ksmserver", "xdg-desktop-portal", "PCHUB",
    };

    private static readonly HashSet<string> NeverInherited = new(StringComparer.Ordinal)
    {
        "firefox", "firefox-bin", "chrome", "chromium", "chromium-browser", "brave", "opera", "vivaldi-bin", "msedge", "librewolf", "epiphany",
        "gnome-terminal-server", "konsole", "xterm", "kitty", "alacritty", "wezterm-gui", "tilix", "xfce4-terminal", "ptyxis",
        "nautilus", "dolphin", "thunar", "nemo", "code", "xdg-open", "gio", "kde-open", "kde-open5", "gnome-text-editor", "gedit", "kate",
    };

    public bool IsTooBroad(string folder)
    {
        string f = PathSafety.Norm(folder);
        if (f == "/" || f.Length <= 1) return true;
        if (SystemRoots.Any(r => f.Equals(r, StringComparison.Ordinal) || PathSafety.IsUnder(f, r))) return true;
        string home = Home;
        string user = Environment.UserName;
        string[] exact =
        [
            home, Path.Combine(home, "Downloads"), Path.Combine(home, "Desktop"), Path.Combine(home, "Documents"),
            Path.Combine(home, ".local"), Path.Combine(home, ".local", "bin"), Path.Combine(home, ".local", "share"), Path.Combine(home, ".config"),
            Path.Combine(home, "Applications"), Path.Combine(home, "AppImages"), Path.Combine(home, ".cache"),
            "/usr", "/usr/local", "/usr/local/bin", "/usr/share", "/opt", "/tmp", "/var", "/var/tmp", "/home", "/root",
            "/mnt", "/media", Path.Combine("/media", user), "/run", "/run/media", Path.Combine("/run/media", user), "/srv", "/snap",
            Path.GetTempPath(),
        ];
        if (exact.Any(e => PathSafety.Norm(e).Equals(f, StringComparison.Ordinal))) return true;

        // The root of a mounted drive (/mnt/x, /media/<user>/x, /run/media/<user>/x).
        string parent = Path.GetDirectoryName(f) ?? "";
        return parent is "/mnt" || parent == Path.Combine("/media", user) || parent == Path.Combine("/run/media", user);
    }

    public bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors) =>
        p.Pid <= 2 || selfAndAncestors.Contains(p.Pid) || ProtectedNames.Contains(p.Name) ||
        (p.Path is { } path && DaemonRoots.Any(r => PathSafety.IsUnder(path, r)));

    /// <summary>
    /// Whether a child process may belong to its parent's app. Browsers, terminals, file managers and editors the
    /// app merely opened stay their own. Interpreters in /usr/bin (python3) do inherit: that's how venvs run.
    /// </summary>
    public bool CanInherit(ProcInfo p)
    {
        if (NeverInherited.Contains(p.Name) || ProtectedNames.Contains(p.Name)) return false;
        if (p.Path is { } path)
        {
            if (path.Contains("firefox", StringComparison.OrdinalIgnoreCase) || path.Contains("chrom", StringComparison.OrdinalIgnoreCase)) return false;
            if (DaemonRoots.Any(r => PathSafety.IsUnder(path, r))) return false;
        }

        return true;
    }

    // ---------------- files and shell ----------------

    /// <summary>Records the download's source address (user.xdg.origin.url), as browsers do. Linux has no quarantine flag.</summary>
    public bool MarkFromInternet(string file, string sourceUrl) => LinuxNative.SetOriginUrl(file, sourceUrl);

    public void OpenWithShell(string target) => XdgOpen(target);

    /// <summary>Opens the folder; file managers don't share one standard way to highlight the file itself.</summary>
    public void RevealInFolder(string file) => XdgOpen(Path.GetDirectoryName(file) ?? file);

    public void OpenTextFile(string file) => XdgOpen(file);

    private static void XdgOpen(string target)
    {
        var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        psi.ArgumentList.Add(target);
        Process.Start(psi)?.Dispose();
    }

    // ---------------- secrets ----------------
    // The desktop keyring (GNOME Keyring / KWallet through the Secret Service) via secret-tool when it's
    // installed. Without it, a file only this user can read (mode 600), like most Linux command-line tools use.

    private const string SecretToolPath = "/usr/bin/secret-tool";
    private static readonly string[] SecretAttrs = ["service", "PCHUB", "account", "claude-api-key"];
    private static string KeyFile => Path.Combine(AppPaths.Data, "claude-api-key");
    private bool? _hasSecret;

    public string? LoadSecret(AppSettings settings)
    {
        if (File.Exists(SecretToolPath) && RunSecretTool(["lookup", .. SecretAttrs], null, out string output) && output.Trim() is { Length: > 0 } fromKeyring)
        {
            return fromKeyring;
        }

        try
        {
            return File.Exists(KeyFile) && File.ReadAllText(KeyFile).Trim() is { Length: > 0 } fromFile ? fromFile : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Checked once, then remembered (asking the keyring on every screen refresh could pop up an unlock prompt).</summary>
    public bool HasSecret(AppSettings settings) => _hasSecret ??= LoadSecret(settings) is not null;

    public bool SaveSecret(AppSettings settings, string? secret)
    {
        bool ok;
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (File.Exists(SecretToolPath)) RunSecretTool(["clear", .. SecretAttrs], null, out _);
            ok = DeleteKeyFile();
            _hasSecret = ok ? false : null;
            return ok;
        }

        string key = secret.Trim();
        if (File.Exists(SecretToolPath) && RunSecretTool(["store", "--label=PCHUB Claude API key", .. SecretAttrs], key, out _))
        {
            DeleteKeyFile(); // the keyring has it now; don't leave a second copy
            _hasSecret = true;
            return true;
        }

        ok = WriteKeyFile(key);
        _hasSecret = ok ? true : null;
        return ok;
    }

    private static bool WriteKeyFile(string key)
    {
        try
        {
            // Create it private before the key goes in, so it is never readable by others even briefly.
            using (File.Create(KeyFile)) { }
            if (LinuxNative.Chmod(KeyFile, 0b110_000_000) != 0) return false; // 0600
            File.WriteAllText(KeyFile, key);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool DeleteKeyFile()
    {
        try
        {
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Runs secret-tool with a timeout. The secret goes in on stdin, never on the command line.</summary>
    private static bool RunSecretTool(string[] args, string? stdin, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(SecretToolPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (stdin is not null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); }
                catch (InvalidOperationException) { /* exited */ }
                return false;
            }

            stdout = read.Wait(1000) ? read.Result : "";
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    // ---------------- drives ----------------

    private static readonly HashSet<string> RealFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "zfs", "bcachefs", "jfs", "reiserfs",
        "ntfs", "ntfs3", "fuseblk", "vfat", "exfat", "msdos",
    };

    public List<DriveStatus> ReadDrives()
    {
        var list = new List<DriveStatus>();
        var devices = MountDevices();
        string user = Environment.UserName;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                string mount = d.Name.Length > 1 ? d.Name.TrimEnd('/') : d.Name;
                bool wanted = mount is "/" or "/home"
                              || PathSafety.IsUnder(mount, "/mnt")
                              || PathSafety.IsUnder(mount, Path.Combine("/media", user))
                              || PathSafety.IsUnder(mount, Path.Combine("/run/media", user));
                if (!wanted || !RealFileSystems.Contains(d.DriveFormat) || !d.IsReady || d.TotalSize <= 0) continue;
                string label = mount switch { "/" => "System", "/home" => "Home", _ => Path.GetFileName(mount) };
                var kind = devices.TryGetValue(mount, out string? dev) ? DiskKind(dev) : DriveKind.Unknown;
                list.Add(new DriveStatus(mount, label, kind, d.TotalSize, d.AvailableFreeSpace, null, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable mount: skip.
            }
        }

        return list;
    }

    /// <summary>mount point → device name (e.g. "nvme0n1p2", "sda1", "dm-0") from /proc/self/mounts.</summary>
    private static Dictionary<string, string> MountDevices()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string line in File.ReadLines("/proc/self/mounts"))
            {
                string[] f = line.Split(' ');
                if (f.Length < 2 || !f[0].StartsWith("/dev/", StringComparison.Ordinal)) continue;
                string device = f[0];
                // /dev/mapper/name is a link to /dev/dm-N.
                if (new FileInfo(device).LinkTarget is { } target) device = target;
                string mount = f[1].Replace("\\040", " ", StringComparison.Ordinal);
                map.TryAdd(mount, Path.GetFileName(device));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown kinds then.
        }

        return map;
    }

    /// <summary>SSD or spinning disk, from the kernel's "rotational" flag of the disk the partition is on.</summary>
    private static DriveKind DiskKind(string device)
    {
        try
        {
            string sys = Path.Combine("/sys/class/block", device);
            string? flag = LinuxGpuMonitor.ReadText(Path.Combine(sys, "queue", "rotational"));
            if (flag is null && new DirectoryInfo(sys).ResolveLinkTarget(true) is { } real && Path.GetDirectoryName(real.FullName) is { } disk)
            {
                flag = LinuxGpuMonitor.ReadText(Path.Combine(disk, "queue", "rotational")); // a partition: ask its disk
            }

            return flag switch { "0" => DriveKind.Ssd, "1" => DriveKind.Hdd, _ => DriveKind.Unknown };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DriveKind.Unknown;
        }
    }

    public bool CanOptimizeDrives => false;
    public Process? StartOptimize(string drive) => null;
    public (string Stage, int Percent)? ReadOptimizeProgress(string drive) => null;
    public string OptimizeLastLine(string drive) => "";

    public void OpenDriveTool()
    {
        foreach (string tool in new[] { "/usr/bin/gnome-disks", "/usr/bin/partitionmanager", "/usr/bin/gparted" })
        {
            if (!File.Exists(tool)) continue;
            Process.Start(new ProcessStartInfo(tool) { UseShellExecute = false })?.Dispose();
            return;
        }

        XdgOpen("/");
    }
}
