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

    /// <summary>
    /// The main GPU: NVIDIA or AMD before Intel, and before boot-time framebuffers (simpledrm, no vendor).
    /// With several AMD GPUs (e.g. a Ryzen's built-in one plus a Radeon card), the one with the most VRAM.
    /// </summary>
    private static (string? Card, string Vendor) FindCard()
    {
        string? best = null;
        string bestVendor = "";
        (int Rank, ulong Vram) bestScore = (-1, 0);
        try
        {
            foreach (string card in Directory.GetDirectories("/sys/class/drm", "card*").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(card).Contains('-')) continue; // connectors like card0-HDMI-A-1
                string dev = Path.Combine(card, "device");
                string vendor = ReadText(Path.Combine(dev, "vendor")) ?? "";
                int rank = vendor switch { "0x10de" or "0x1002" => 2, "0x8086" => 1, _ => 0 };
                ulong vram = LinuxNative.ReadSysNumber(Path.Combine(dev, "mem_info_vram_total")) ?? 0;
                if (rank > bestScore.Rank || (rank == bestScore.Rank && vram > bestScore.Vram))
                {
                    best = dev;
                    bestVendor = vendor;
                    bestScore = (rank, vram);
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

    // pid → kernel name seen at the last scan. A process that exec()s keeps its pid and start time but changes
    // program (sh → python), so a changed name means its path must be read again.
    private Dictionary<int, string> _comm = new();

    public IReadOnlyDictionary<int, ProcInfo> ScanProcesses()
    {
        lock (_scanGate)
        {
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories("/proc");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return _cache;
            }

            var next = new Dictionary<int, ProcInfo>(Math.Max(16, _cache.Count + 32));
            var comm = new Dictionary<int, string>(Math.Max(16, _comm.Count + 32));
            foreach (string dir in dirs)
            {
                string leaf = Path.GetFileName(dir);
                if (leaf.Length == 0 || !char.IsAsciiDigit(leaf[0])) continue;
                if (!int.TryParse(leaf, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 1) continue;
                if (!LinuxNative.TryReadStat(pid, out var st)) continue;
                if (pid == 2 || st.ParentPid == 2) continue; // kernel threads
                long created = LinuxNative.StartFileTime(st.StartTicks);
                comm[pid] = st.Comm;
                if (_cache.TryGetValue(pid, out var known) && known.CreateTime == created &&
                    _comm.TryGetValue(pid, out string? oldComm) && oldComm == st.Comm)
                {
                    next[pid] = known with { ParentPid = st.ParentPid };
                    continue;
                }

                string? path = LinuxNative.ReadLink($"/proc/{pid}/exe");
                string name = path is not null ? Path.GetFileName(path) : st.Comm;
                // Interpreters run from a shared system folder; the script they run tells which app they serve.
                string? scriptDir = name.StartsWith("python", StringComparison.Ordinal) ? ScriptFolder(pid) : null;
                next[pid] = new ProcInfo(pid, st.ParentPid, name, path, created, scriptDir);
            }

            _cache = next;
            _comm = comm;
            return next;
        }
    }

    /// <summary>
    /// The folder of the script a Python process runs ("python main.py" in ComfyUI's folder → that folder).
    /// Null for "python -m", "python -c", an interactive prompt, or anything unreadable.
    /// </summary>
    private static string? ScriptFolder(int pid)
    {
        string[] argv;
        try
        {
            argv = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (int i = 1; i < argv.Length; i++)
        {
            string a = argv[i];
            if (a is "-m" or "-c" or "-") return null;
            if (a is "-X" or "-W" or "-Q")
            {
                i++; // options that take a value
                continue;
            }

            if (a.StartsWith('-')) continue;

            string? cwd = LinuxNative.ReadLink($"/proc/{pid}/cwd");
            string script = Path.IsPathRooted(a) ? a : cwd is null ? "" : Path.Combine(cwd, a);
            return script.Length > 0 && File.Exists(script) ? Path.GetDirectoryName(Path.GetFullPath(script)) : null;
        }

        return null;
    }

    /// <summary>
    /// Asks each process to quit (SIGTERM), waits, then forces the rest (SIGKILL). Only the current user's own
    /// processes are touched. Each process is pinned with a pidfd (Linux 5.3+) right after its start time is
    /// verified, so a signal can never reach a different program that reused the PID; older kernels fall back
    /// to re-checking the start time before every signal.
    /// </summary>
    public Task<EndResult> EndProcessesAsync(IReadOnlyList<ProcInfo> procs, int graceSeconds) => Task.Run(async () =>
    {
        var errors = new List<string>();
        uint me;
        try
        {
            me = LinuxNative.GetEuid();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new EndResult(0, 0, ["This Linux has no glibc, so PCHUB can't end processes here: " + ex.Message]);
        }

        if (me == 0)
        {
            return new EndResult(0, 0, ["PCHUB is running as root, so it won't end anything (that could reach system programs). Start PCHUB as your normal user."]);
        }

        var self = SelfAndAncestors();
        var targets = new List<(ProcInfo Proc, int Fd)>();
        foreach (var p in procs.OrderByDescending(p => p.CreateTime))
        {
            if (p.CreateTime == 0)
            {
                errors.Add($"Couldn't verify {p.Name} ({p.Pid}), so PCHUB left it alone.");
                continue;
            }

            if (IsProtected(p, self)) continue;
            if (LinuxNative.OwnerUid(p.Pid) != me)
            {
                if (IsSameProcess(p)) errors.Add(OtherUserMessage(p));
                continue;
            }

            if (!IsSameProcess(p)) continue; // already gone
            int fd = LinuxNative.PidfdOpen(p.Pid);
            if (fd >= 0 && !IsSameProcess(p))
            {
                LinuxNative.Close(fd); // the PID was reused between the check and the pin
                continue;
            }

            targets.Add((p, fd));
        }

        try
        {
            foreach (var (p, fd) in targets) Signal(p, fd, LinuxNative.SigTerm);

            if (graceSeconds > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(graceSeconds);
                while (DateTime.UtcNow < deadline && targets.Any(t => IsSameProcess(t.Proc))) await Task.Delay(250).ConfigureAwait(false);
            }

            int closed = 0, killed = 0;
            foreach (var (p, fd) in targets)
            {
                if (!IsSameProcess(p))
                {
                    closed++;
                    continue;
                }

                if (Signal(p, fd, LinuxNative.SigKill)) killed++;
                else if (!IsSameProcess(p)) closed++; // it exited between the check and the signal
                else errors.Add($"Couldn't end {p.Name} ({p.Pid}).");
            }

            return new EndResult(closed, killed, errors);
        }
        finally
        {
            foreach (var (_, fd) in targets)
            {
                if (fd >= 0) LinuxNative.Close(fd);
            }
        }
    });

    /// <summary>Signals through the pidfd when there is one (can't hit a reused PID), else by PID after a fresh check.</summary>
    private static bool Signal(ProcInfo p, int fd, int signal) =>
        fd >= 0 ? LinuxNative.PidfdSendSignal(fd, signal) : IsSameProcess(p) && p.Pid > 1 && LinuxNative.Kill(p.Pid, signal) == 0;

    /// <summary>Explains a process that runs as another user, naming the systemd service when it is one (e.g. Ollama).</summary>
    private static string OtherUserMessage(ProcInfo p)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{p.Pid}/cgroup"))
            {
                int at = line.IndexOf("/system.slice/", StringComparison.Ordinal);
                if (at < 0) continue;
                string unit = line[(at + 14)..].Split('/')[0];
                if (unit.EndsWith(".service", StringComparison.Ordinal))
                {
                    string name = unit[..^8];
                    return $"{p.Name} runs as the system service \"{name}\", so PCHUB leaves it alone. To stop it: sudo systemctl stop {name}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall through to the general message
        }

        return $"{p.Name} ({p.Pid}) runs as another user, so PCHUB leaves it alone.";
    }

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
        bool appImage = file.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase);
        if (appImage && File.Exists(file) && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) == 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(file)} isn't allowed to run yet. In your file manager: right-click it, Properties, allow executing as a program (or run: chmod +x \"{file}\"), then press Start again.");
        }

        string shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        string command = (script ? shell + " " : "") + Quote(file) + (string.IsNullOrWhiteSpace(t.Arguments) ? "" : " " + t.Arguments);

        // Its own session, so closing the terminal PCHUB was started from (or Ctrl+C there) doesn't take the app
        // down with it. Without --fork, setsid keeps the same PID, so PCHUB still tracks the right process.
        if (File.Exists("/usr/bin/setsid")) command = "/usr/bin/setsid " + command;
        command = logPath is not null
            ? $"exec {command} >> {Quote(logPath)} 2>&1" // the shell owns the log, so closing PCHUB never breaks it
            : "exec " + command;

        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        // AppImages need FUSE 2, which newer Ubuntu releases don't install; they can unpack themselves instead.
        if (appImage && !HasFuse2()) psi.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";

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

    private static bool HasFuse2()
    {
        foreach (string dir in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/lib/x86_64-linux-gnu", "/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/lib64", "/lib" })
        {
            if (File.Exists(Path.Combine(dir, "libfuse.so.2"))) return true;
        }

        return false;
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
                // The name must continue with a separator or version number, so "january-notes.AppImage" isn't Jan.
                var match = KnownAppImages.FirstOrDefault(k => lower.StartsWith(k.Fragment, StringComparison.Ordinal) &&
                    lower.Length > k.Fragment.Length && (lower[k.Fragment.Length] is '-' or '_' or '.' || char.IsAsciiDigit(lower[k.Fragment.Length])));
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

    /// <summary>System folders: never an app's folder.</summary>
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

    /// <summary>Where the system's own programs live. Of these, PCHUB only ever ends the runtimes AI apps run on.</summary>
    private static readonly string[] SystemProgramRoots = ["/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/lib", "/lib", "/usr/lib64", "/lib64", "/usr/libexec"];

    /// <summary>Program names in system folders that AI apps run on (a venv's python is really /usr/bin/python3.x).</summary>
    private static bool IsAppRuntime(string name) =>
        name.StartsWith("python", StringComparison.Ordinal) || name is "node" or "java" or "ollama" or "uv" or "deno" or "bun";

    private static readonly string[] ProtectedPrefixes = ["gnome-", "xdg-desktop-portal", "kwin", "plasma", "xfce4-", "gvfs", "at-spi", "ibus", "fcitx"];

    private static readonly HashSet<string> NeverInherited = new(StringComparer.Ordinal)
    {
        "bash", "sh", "dash", "zsh", "fish", "tmux", "screen", "ssh-agent", "gpg-agent", "gnome-keyring-daemon", "ssh",
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

    /// <summary>
    /// Never ended: PCHUB itself and its parents, the desktop's own programs, anything whose program file can't be
    /// read (so it can't be identified), and system programs other than the runtimes AI apps use.
    /// </summary>
    public bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors)
    {
        if (p.Pid <= 2 || selfAndAncestors.Contains(p.Pid) || ProtectedNames.Contains(p.Name)) return true;
        if (ProtectedPrefixes.Any(x => p.Name.StartsWith(x, StringComparison.Ordinal))) return true;
        if (p.Path is not { } path) return true;
        if (DaemonRoots.Any(r => PathSafety.IsUnder(path, r))) return true;
        return SystemProgramRoots.Any(r => PathSafety.IsUnder(path, r)) && !IsAppRuntime(p.Name);
    }

    /// <summary>
    /// Whether a child process may belong to its parent's app. Browsers, terminals, file managers and editors the
    /// app merely opened stay their own. Interpreters in /usr/bin (python3) do inherit: that's how venvs run.
    /// </summary>
    public bool CanInherit(ProcInfo p)
    {
        if (NeverInherited.Contains(p.Name) || ProtectedNames.Contains(p.Name)) return false;
        if (ProtectedPrefixes.Any(x => p.Name.StartsWith(x, StringComparison.Ordinal))) return false;
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
    // A marker file (no secret in it) records which of the two holds the key, so checking "is a key saved?"
    // never has to ask the keyring, which could pop up an unlock prompt.

    private static readonly string[] SecretAttrs = ["service", "PCHUB", "account", "claude-api-key"];
    private static string KeyFile => Path.Combine(AppPaths.Data, "claude-api-key");
    private static string KeyringMarker => Path.Combine(AppPaths.Data, "claude-api-key.in-keyring");

    /// <summary>secret-tool from the PATH (it lives in /usr/bin on most distros, elsewhere on NixOS and others).</summary>
    private static string? SecretTool()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries).Append("/usr/bin"))
        {
            string p = Path.Combine(dir, "secret-tool");
            if (Path.IsPathFullyQualified(p) && File.Exists(p)) return p;
        }

        return null;
    }

    public string? LoadSecret(AppSettings settings)
    {
        if (File.Exists(KeyringMarker))
        {
            return SecretTool() is { } tool && RunSecretTool(tool, ["lookup", .. SecretAttrs], null, out string output) && output.Trim() is { Length: > 0 } k ? k : null;
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

    public bool HasSecret(AppSettings settings) => File.Exists(KeyringMarker) || File.Exists(KeyFile);

    public bool SaveSecret(AppSettings settings, string? secret)
    {
        string? tool = SecretTool();
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (tool is not null) RunSecretTool(tool, ["clear", .. SecretAttrs], null, out _);
            return DeleteFile(KeyringMarker) & DeleteFile(KeyFile);
        }

        string key = secret.Trim();
        if (tool is not null && RunSecretTool(tool, ["store", "--label=PCHUB Claude API key", .. SecretAttrs], key, out _) && WritePrivate(KeyringMarker, ""))
        {
            DeleteFile(KeyFile); // the keyring has it now; don't leave a second copy
            return true;
        }

        // Keyring missing or refused: make sure an older keyring copy can't win over the new key.
        if (tool is not null) RunSecretTool(tool, ["clear", .. SecretAttrs], null, out _);
        DeleteFile(KeyringMarker);

        // Never put the key in a shared temp folder (PCHUB falls back to one if its data folder can't be made).
        if (PathSafety.IsUnder(PathSafety.Norm(AppPaths.Data), PathSafety.Norm(Path.GetTempPath()))) return false;
        return WritePrivate(KeyFile, key);
    }

    /// <summary>Writes a file that only this user can read (created 0600, then moved into place atomically).</summary>
    private static bool WritePrivate(string path, string content)
    {
        string tmp = path + ".tmp";
        try
        {
            File.Delete(tmp);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using (var fs = new FileStream(tmp, options))
            using (var w = new StreamWriter(fs))
            {
                w.Write(content);
            }

            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteFile(tmp);
            return false;
        }
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Runs secret-tool with a timeout. The secret goes in on stdin, never on the command line.</summary>
    private static bool RunSecretTool(string tool, string[] args, string? stdin, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(tool)
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
