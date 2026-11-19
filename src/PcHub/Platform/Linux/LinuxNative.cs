using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcHub.Platform.Linux;

/// <summary>Linux system calls and /proc readers used by PCHUB. Everything here works without root.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxNative
{
    // glibc's real file name ("libc.so" is a linker script that dlopen can't load). The linux-x64/arm64 builds
    // target glibc distributions (Ubuntu, Debian, Fedora, Arch, Mint...), not musl ones like Alpine.
    private const string LibC = "libc.so.6";

    public const int SigTerm = 15;
    public const int SigKill = 9;
    private const int ScClkTck = 2; // _SC_CLK_TCK in glibc and musl

    [LibraryImport(LibC, EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    [LibraryImport(LibC, EntryPoint = "geteuid")]
    public static partial uint GetEuid();

    [LibraryImport(LibC, EntryPoint = "sysconf")]
    private static partial long SysConf(int name);

    [LibraryImport(LibC, EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int SetXattr(string path, string name, byte* value, nuint size, int flags);

    [LibraryImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall2(long number, long a, long b);

    [LibraryImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall4(long number, long a, long b, long c, long d);

    [LibraryImport(LibC, EntryPoint = "close")]
    private static partial int CloseFd(int fd);

    // The same numbers on x86-64 and arm64 (both use the generic table for these newer calls).
    private const long SysPidfdOpen = 434;
    private const long SysPidfdSendSignal = 424;

    /// <summary>
    /// A handle that stays tied to this exact process even if it exits and its PID is reused (Linux 5.3+).
    /// -1 when the kernel is older or the process is gone.
    /// </summary>
    public static int PidfdOpen(int pid)
    {
        if (pid <= 1) return -1;
        try
        {
            long fd = Syscall2(SysPidfdOpen, pid, 0);
            return fd is >= 0 and <= int.MaxValue ? (int)fd : -1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>Signals the process behind a pidfd; fails (instead of hitting a stranger) if it already exited.</summary>
    public static bool PidfdSendSignal(int fd, int signal) => Syscall4(SysPidfdSendSignal, fd, signal, 0, 0) == 0;

    public static void Close(int fd)
    {
        if (fd >= 0) _ = CloseFd(fd);
    }

    private static readonly long ClockTicks = TicksPerSecond();
    private static readonly DateTime? BootTimeUtc = ReadBootTime();
    public static readonly long PageSize = Environment.SystemPageSize;

    private static long TicksPerSecond()
    {
        try
        {
            long t = SysConf(ScClkTck);
            return t > 0 ? t : 100;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 100;
        }
    }

    private static DateTime? ReadBootTime()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long secs))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No /proc/stat: start times stay 0, so PCHUB won't end processes (it can't verify them).
        }

        return null;
    }

    /// <summary>The fields PCHUB needs from /proc/&lt;pid&gt;/stat.</summary>
    public readonly record struct Stat(char State, int ParentPid, long StartTicks, string Comm);

    public static bool TryReadStat(int pid, out Stat stat)
    {
        stat = default;
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid}/stat");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // "pid (comm) state ppid ...": comm may itself contain spaces and ')' so split at the LAST ')'.
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close < open || close + 2 >= text.Length) return false;
        string comm = text[(open + 1)..close];
        string[] f = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // After comm: [0]=state [1]=ppid ... [19]=starttime (field 22 overall).
        if (f.Length < 20 || f[0].Length != 1) return false;
        if (!int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ppid)) return false;
        if (!long.TryParse(f[19], NumberStyles.None, CultureInfo.InvariantCulture, out long start)) return false;
        stat = new Stat(f[0][0], ppid, start, comm);
        return true;
    }

    /// <summary>Process start as a UTC FILETIME, like the other platforms (0 if unknown).</summary>
    public static long StartFileTime(long startTicks)
    {
        if (BootTimeUtc is not { } boot) return 0;
        try
        {
            return boot.AddTicks(startTicks * (TimeSpan.TicksPerSecond / ClockTicks)).ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    /// <summary>The real UID of the process's owner, or null if unreadable.</summary>
    public static uint? OwnerUid(int pid)
    {
        try
        {
            foreach (string line in File.ReadLines($"/proc/{pid}/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                string[] parts = line[4..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint uid) ? uid : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // gone or not ours to read
        }

        return null;
    }

    /// <summary>Resolves a /proc symlink (exe, cwd). Null when the process is gone or belongs to someone else.</summary>
    public static string? ReadLink(string path)
    {
        try
        {
            var target = new FileInfo(path).LinkTarget;
            if (target is null) return null;
            // A deleted executable reads as "/path/to/file (deleted)".
            return target.EndsWith(" (deleted)", StringComparison.Ordinal) ? target[..^10] : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ulong ResidentBytes(int pid)
    {
        try
        {
            string[] f = File.ReadAllText($"/proc/{pid}/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return f.Length > 1 && ulong.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong pages) ? pages * (ulong)PageSize : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static ulong? MemTotalBytes()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                string kb = line[9..].Trim().Split(' ')[0];
                return ulong.TryParse(kb, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) ? v * 1024 : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall through
        }

        return null;
    }

    /// <summary>Reads a small sysfs number (e.g. VRAM used); null if missing or not a number.</summary>
    public static ulong? ReadSysNumber(string path)
    {
        try
        {
            string s = File.ReadAllText(path).Trim();
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v) ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records where a download came from, the freedesktop way (file managers show it). Best effort.</summary>
    public static unsafe bool SetOriginUrl(string file, string url)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(url);
        try
        {
            fixed (byte* b = bytes)
            {
                return SetXattr(file, "user.xdg.origin.url", b, (nuint)bytes.Length, 0) == 0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
