using System.Diagnostics;
using PcHub.Services;

namespace PcHub.Platform;

/// <summary>
/// A process seen in one scan. CreateTime is a UTC FILETIME and, with Pid, identifies it uniquely.
/// Cwd (Linux only) is the working folder of Python interpreters, which run from a shared system path.
/// </summary>
public sealed record ProcInfo(int Pid, int ParentPid, string Name, string? Path, long CreateTime, string? Cwd = null);

/// <summary>One GPU reading. Optional fields are null when the platform can't read them.</summary>
public sealed record GpuSample(
    ulong UsedBytes,
    ulong TotalBytes,
    int LoadPercent,
    int? TemperatureC,
    double? PowerWatts,
    double? PowerLimitWatts,
    int? FanPercent,
    int? CoreMhz,
    int? MemoryMhz);

/// <summary>Per-process GPU use. DedicatedBytes is -1 when the OS reported an impossible value.</summary>
public readonly record struct GpuProcUsage(long DedicatedBytes, double UtilPercent);

/// <summary>A slower reading taken with each process scan.</summary>
public sealed record GpuScanReading(Dictionary<int, GpuProcUsage> PerProcess, GpuSample? Sample, string? Error);

public enum DriveKind
{
    Unknown,
    Ssd,
    Hdd,
}

public sealed record DriveStatus(
    string Letter,
    string Label,
    DriveKind Kind,
    long TotalBytes,
    long FreeBytes,
    int? FragmentationPercent,
    DateTime? LastOptimized);

public sealed record EndResult(int Closed, int Killed, IReadOnlyList<string> Errors);

/// <summary>The GPU being watched.</summary>
public interface IGpuMonitor : IDisposable
{
    string Name { get; }
    string Detail { get; }

    /// <summary>Why some readings are missing, if they are.</summary>
    string? Note { get; }

    /// <summary>Temperature, power, fan and clocks are available.</summary>
    bool HasSensors { get; }

    /// <summary>"VRAM" on PCs with a graphics card; "GPU memory" where memory is shared (Apple Silicon).</summary>
    string MemoryLabel { get; }

    /// <summary>True when per-process numbers are GPU memory; false when they are the app's RAM (macOS).</summary>
    bool PerProcessIsGpuMemory { get; }

    ulong TotalBytes { get; }

    /// <summary>Called every tick; null if this platform only has the slower scan reading.</summary>
    GpuSample? ReadFast();

    /// <summary>Called with each process scan.</summary>
    GpuScanReading ReadScan(IReadOnlyDictionary<int, ProcInfo> processes);

    /// <summary>Memory in use right now (used to measure before/after freeing).</summary>
    ulong? UsedBytesNow();
}

/// <summary>
/// Everything that differs between Windows, macOS and Linux. The rest of PCHUB talks only to this.
/// </summary>
public interface IPlatform
{
    string OsName { get; }
    bool IsWindows { get; }
    bool IsMac { get; }
    bool IsLinux { get; }

    IGpuMonitor CreateGpuMonitor(AppSettings settings);

    /// <summary>All processes, with path and start time where readable.</summary>
    IReadOnlyDictionary<int, ProcInfo> ScanProcesses();

    /// <summary>
    /// Ends exactly these processes (never others), asking them to close first. Each process is re-checked
    /// against its recorded start time before it is ended, so a reused PID is never touched.
    /// </summary>
    Task<EndResult> EndProcessesAsync(IReadOnlyList<ProcInfo> processes, int graceSeconds);

    /// <summary>How to start a tool. Hidden tools send their output to logPath.</summary>
    ProcessStartInfo BuildStart(ToolDefinition tool, string workingDirectory, string? logPath);

    // ---- app detection ----
    void DetectFixedApps(List<ToolDefinition> found);
    IEnumerable<string> ScanRoots();
    string? VenvPython(string folder);
    string? FindExecutable(string folder, string stem);

    // ---- safety rules ----
    bool IsTooBroad(string folder);
    bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors);
    bool CanInherit(ProcInfo p);
    StringComparison PathComparison { get; }

    // ---- files and shell ----
    bool MarkFromInternet(string file, string sourceUrl);
    void OpenWithShell(string target);
    void RevealInFolder(string file);
    void OpenTextFile(string file);

    // ---- secrets ----
    string? LoadSecret(AppSettings settings);
    /// <summary>Whether a secret is stored, without reading it (no Keychain prompt, no decryption).</summary>
    bool HasSecret(AppSettings settings);
    bool SaveSecret(AppSettings settings, string? secret);

    // ---- drives ----
    List<DriveStatus> ReadDrives();
    bool CanOptimizeDrives { get; }
    Process? StartOptimize(string drive);
    (string Stage, int Percent)? ReadOptimizeProgress(string drive);
    string OptimizeLastLine(string drive);
    void OpenDriveTool();
}

public static class Os
{
    /// <summary>The platform PCHUB is running on.</summary>
    public static IPlatform Current { get; } = Create();

    private static IPlatform Create()
    {
        if (OperatingSystem.IsWindows()) return new Windows.WindowsPlatform();
        if (OperatingSystem.IsMacOS()) return new Mac.MacPlatform();
        if (OperatingSystem.IsLinux()) return new Linux.LinuxPlatform();
        throw new PlatformNotSupportedException("PCHUB runs on Windows, macOS and Linux.");
    }
}
