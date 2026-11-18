using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PcHub.Services;

namespace PcHub.Platform.Windows;

/// <summary>GPU on Windows: NVML for NVIDIA sensors, Windows' GPU counters for VRAM/load per process (any vendor).</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGpuMonitor : IGpuMonitor
{
    private readonly GpuContext _gpu;
    private readonly GpuProcessCounters _counters;
    private readonly object _nvmlGate = new();

    public WindowsGpuMonitor(string? preferredLuid)
    {
        Nvml.RegisterResolver();
        _gpu = GpuContext.CreateSafe(preferredLuid);
        _counters = new GpuProcessCounters(_gpu.Adapter?.LuidTag);
    }

    public string Name => _gpu.Name;
    public string Detail => _gpu.Detail;
    public string? Note => _gpu.Note;
    public bool HasSensors => _gpu.HasNvml;
    public string MemoryLabel => "VRAM";
    public bool PerProcessIsGpuMemory => true;
    public ulong TotalBytes => _gpu.TotalBytes;

    public GpuSample? ReadFast()
    {
        if (!_gpu.HasNvml) return null;
        lock (_nvmlGate) return _gpu.Nvml!.Read();
    }

    public GpuScanReading ReadScan(IReadOnlyDictionary<int, ProcInfo> processes)
    {
        CounterReading reading;
        lock (_counters) reading = _counters.Read();
        GpuSample? sample = null;
        if (!_gpu.HasNvml && _gpu.Adapter is not null)
        {
            sample = new GpuSample((ulong)Math.Max(0, reading.AdapterDedicatedBytes), _gpu.TotalBytes,
                (int)Math.Round(reading.AdapterUtilPercent), null, null, null, null, null, null);
        }

        return new GpuScanReading(reading.PerProcess, sample, _counters.Error);
    }

    public ulong? UsedBytesNow()
    {
        if (_gpu.HasNvml)
        {
            lock (_nvmlGate) return _gpu.Nvml!.Read()?.UsedBytes;
        }

        long bytes;
        lock (_counters) bytes = _counters.ReadAdapterBytes();
        return bytes >= 0 ? (ulong)bytes : null;
    }

    public void Dispose() => _gpu.Dispose();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsPlatform : IPlatform
{
    private readonly ProcessScanner _scanner = new();
    private readonly object _scanGate = new();

    public string OsName => "Windows";
    public bool IsWindows => true;
    public bool IsMac => false;
    public StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    public IGpuMonitor CreateGpuMonitor(AppSettings settings) => new WindowsGpuMonitor(settings.AdapterLuid);

    public IReadOnlyDictionary<int, ProcInfo> ScanProcesses()
    {
        lock (_scanGate) return _scanner.Scan();
    }

    // ---------------- starting and ending ----------------

    public ProcessStartInfo BuildStart(ToolDefinition t, string workDir, string? logPath)
    {
        bool isBareName = !t.FileName.Contains('\\') && !t.FileName.Contains('/');
        string ext = Path.GetExtension(t.FileName).ToLowerInvariant();
        if (logPath is not null)
        {
            // cmd.exe owns the log file, so closing PCHUB never breaks the tool's output pipe.
            string inner = ext is ".bat" or ".cmd" ? $"call \"{t.FileName}\" {t.Arguments}" : $"\"{t.FileName}\" {t.Arguments}";
            return new ProcessStartInfo("cmd.exe", $"/d /s /c \"{inner} >> \"{logPath}\" 2>&1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
        }

        if (ext is ".bat" or ".cmd")
        {
            return new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{t.FileName}\" {t.Arguments}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workDir,
            };
        }

        // Documents and shortcuts must go through the shell (they can't take extra settings such as BROWSER).
        return new ProcessStartInfo(t.FileName, t.Arguments)
        {
            UseShellExecute = !(ext is ".exe" || isBareName),
            WorkingDirectory = workDir,
        };
    }

    public Task<EndResult> EndProcessesAsync(IReadOnlyList<ProcInfo> procs, int graceSeconds) =>
        Task.Run(() => EndCoreAsync(procs, graceSeconds));

    /// <summary>
    /// Each target is opened once and checked against its recorded start time; holding that handle means
    /// Windows can't give the PID to another program while we wait. Windows are asked to close first.
    /// </summary>
    private async Task<EndResult> EndCoreAsync(IReadOnlyList<ProcInfo> procs, int graceSeconds)
    {
        var errors = new List<string>();
        var self = SelfAndAncestors();
        var targets = new List<(ProcInfo Info, nint Handle)>();
        try
        {
            foreach (var p in procs.OrderByDescending(p => p.CreateTime))
            {
                if (IsProtected(p, self)) continue;
                nint h = OpenVerified(p);
                if (h != 0) targets.Add((p, h));
            }

            bool askedAny = false;
            foreach (var (info, _) in targets)
            {
                try
                {
                    using var proc = Process.GetProcessById(info.Pid); // safe: our open handle pins this PID
                    if (proc.MainWindowHandle != 0 && proc.CloseMainWindow()) askedAny = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Already gone or no window; handled below.
                }
            }

            if (askedAny && graceSeconds > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(graceSeconds);
                while (DateTime.UtcNow < deadline && targets.Any(x => IsAlive(x.Handle)))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            int closed = 0, killed = 0;
            foreach (var (info, h) in targets)
            {
                if (!IsAlive(h))
                {
                    closed++;
                    continue;
                }

                if (Win32.TerminateProcess(h, 1))
                {
                    killed++;
                }
                else
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (IsAlive(h)) errors.Add($"Couldn't end {info.Name} ({info.Pid}): {new Win32Exception(err).Message}. It may be running as administrator.");
                }
            }

            foreach (var (_, h) in targets) Win32.WaitForSingleObject(h, 3000);
            return new EndResult(closed, killed, errors);
        }
        finally
        {
            foreach (var (_, h) in targets) Win32.CloseHandle(h);
        }
    }

    private static nint OpenVerified(ProcInfo p)
    {
        if (p.CreateTime == 0) return 0;
        nint h = Win32.OpenProcess(Win32.ProcessTerminate | Win32.ProcessQueryLimitedInformation | Win32.Synchronize, false, (uint)p.Pid);
        if (h == 0) return 0;
        if (Win32.GetProcessTimes(h, out long created, out _, out _, out _) && created == p.CreateTime) return h;
        Win32.CloseHandle(h);
        return 0;
    }

    private static bool IsAlive(nint handle) => Win32.WaitForSingleObject(handle, 0) == Win32.WaitTimeout;

    private HashSet<int> SelfAndAncestors()
    {
        var set = new HashSet<int> { Environment.ProcessId };
        var all = ScanProcesses();
        int pid = Environment.ProcessId;
        for (int i = 0; i < 32 && all.TryGetValue(pid, out var p) && p.ParentPid > 4 && set.Add(p.ParentPid); i++) pid = p.ParentPid;
        return set;
    }

    // ---------------- app detection ----------------

    private static readonly (string Exe, string Name)[] KnownDesktopApps =
    [
        ("LM Studio.exe", "LM Studio"),
        ("Jan.exe", "Jan"),
        ("ComfyUI.exe", "ComfyUI Desktop"),
        ("Invoke Community Edition.exe", "Invoke"),
        ("Pinokio.exe", "Pinokio"),
        ("AnythingLLM.exe", "AnythingLLM"),
        ("Msty.exe", "Msty"),
    ];

    public void DetectFixedApps(List<ToolDefinition> found)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (string dir in new[] { Path.Combine(local, "Programs", "Ollama"), Path.Combine(pf, "Ollama") })
        {
            string exe = Path.Combine(dir, "ollama.exe");
            if (!File.Exists(exe)) continue;
            found.Add(new ToolDefinition
            {
                Id = "ollama", Name = "Ollama", Kind = "Ollama",
                FileName = exe, Arguments = "serve", WorkingDirectory = dir, Hidden = true,
                MatchPaths = [dir], Port = 11434,
            });
            break;
        }

        string programs = Path.Combine(local, "Programs");
        if (Directory.Exists(programs))
        {
            foreach (string sub in SafeSubDirs(programs))
            {
                foreach (var (exeName, name) in KnownDesktopApps)
                {
                    string exe = Path.Combine(sub, exeName);
                    if (!File.Exists(exe)) continue;
                    var t = new ToolDefinition { Id = ToolDetector.Slug(name), Name = name, FileName = exe, WorkingDirectory = sub, MatchPaths = [sub] };
                    if (name == "LM Studio" && Directory.Exists(Path.Combine(home, ".lmstudio"))) t.MatchPaths.Add(Path.Combine(home, ".lmstudio"));
                    found.Add(t);
                }
            }
        }

        foreach (string chat in new[] { Path.Combine(home, "gpt4all", "bin", "chat.exe"), Path.Combine(pf, "gpt4all", "bin", "chat.exe") })
        {
            if (!File.Exists(chat)) continue;
            string root = Path.GetDirectoryName(Path.GetDirectoryName(chat))!;
            found.Add(new ToolDefinition { Id = "gpt4all", Name = "GPT4All", FileName = chat, WorkingDirectory = root, MatchPaths = [root] });
            break;
        }
    }

    public IEnumerable<string> ScanRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException)
            {
                // drive went away
            }
        }

        foreach (var sf in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            string p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) roots.Add(downloads);
        return roots;
    }

    public string? VenvPython(string folder)
    {
        foreach (string v in new[] { "venv", ".venv" })
        {
            string p = Path.Combine(folder, v, "Scripts", "python.exe");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public string? FindExecutable(string folder, string stem)
    {
        string p = Path.Combine(folder, "Scripts", stem + ".exe");
        return File.Exists(p) ? p : null;
    }

    private static string[] SafeSubDirs(string parent)
    {
        try
        {
            return Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    // ---------------- safety rules ----------------

    private static readonly string WindowsDir = PathSafety.Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "winlogon.exe", "lsass.exe", "services.exe", "smss.exe", "svchost.exe",
        "explorer.exe", "dwm.exe", "sihost.exe", "fontdrvhost.exe", "ctfmon.exe", "taskmgr.exe",
    };

    private static readonly HashSet<string> NeverInherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "iexplore.exe",
        "explorer.exe", "notepad.exe", "WindowsTerminal.exe", "code.exe",
    };

    public bool IsTooBroad(string folder)
    {
        string f = PathSafety.Norm(folder);
        if (f.Length <= 3) return true; // "C:" or "C:\"
        if (f.Equals(WindowsDir, StringComparison.OrdinalIgnoreCase) || PathSafety.IsUnder(f, WindowsDir)) return true;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] exact =
        [
            home, Path.Combine(home, "Downloads"), Path.Combine(home, "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local, Path.Combine(local, "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),
        ];
        return exact.Any(e => !string.IsNullOrEmpty(e) && PathSafety.Norm(e).Equals(f, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors) =>
        selfAndAncestors.Contains(p.Pid) || ProtectedNames.Contains(p.Name) ||
        (p.Path is { } path && PathSafety.IsUnder(PathSafety.Norm(path), WindowsDir));

    public bool CanInherit(ProcInfo p)
    {
        if (NeverInherited.Contains(p.Name) || ProtectedNames.Contains(p.Name)) return false;
        if (p.Path is { } path && PathSafety.IsUnder(PathSafety.Norm(path), WindowsDir))
        {
            // Console helpers are fine to inherit; other Windows programs are not.
            return p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    // ---------------- files and shell ----------------

    public bool MarkFromInternet(string file, string sourceUrl)
    {
        try
        {
            File.WriteAllText(file + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://github.com/\r\nHostUrl={sourceUrl}\r\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false; // FAT32/exFAT drives can't store the mark
        }
    }

    public void OpenWithShell(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    public void RevealInFolder(string file) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();

    public void OpenTextFile(string file) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true })?.Dispose();

    // ---------------- secrets (DPAPI) ----------------

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCHUB.ClaudeKey.v1");

    public bool HasSecret(AppSettings s) => !string.IsNullOrEmpty(s.ClaudeKeyProtected);

    public string? LoadSecret(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.ClaudeKeyProtected)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ClaudeKeyProtected), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // saved under another Windows user, or damaged
        }
    }

    public bool SaveSecret(AppSettings s, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            s.ClaudeKeyProtected = null;
            return true;
        }

        try
        {
            s.ClaudeKeyProtected = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret.Trim()), Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ---------------- drives ----------------

    public List<DriveStatus> ReadDrives() => DriveService.Read();
    public bool CanOptimizeDrives => true;
    public Process? StartOptimize(string drive) => OptimizeRunner.Start(drive);
    public (string Stage, int Percent)? ReadOptimizeProgress(string drive) => OptimizeRunner.ReadProgress(drive);
    public string OptimizeLastLine(string drive) => OptimizeRunner.LastLine(drive);
    public void OpenDriveTool() => DriveService.OpenOptimizeDrives();
}
