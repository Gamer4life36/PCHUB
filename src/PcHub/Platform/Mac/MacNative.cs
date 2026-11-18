using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace PcHub.Platform.Mac;

/// <summary>macOS system calls used by PCHUB (libproc, sysctl, signals, extended attributes, Keychain).</summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const int ProcPidTbsdInfo = 3;
    public const int ProcPidTaskInfo = 4;
    public const int SigTerm = 15;
    public const int SigKill = 9;
    public const int ErrSecItemNotFound = -25300;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcBsdInfo
    {
        public uint Flags;
        public uint Status;
        public uint XStatus;
        public uint Pid;
        public uint PPid;
        public uint Uid;
        public uint Gid;
        public uint RUid;
        public uint RGid;
        public uint SvUid;
        public uint SvGid;
        public uint Rfu1;
        public fixed byte Comm[16];
        public fixed byte Name[32];
        public uint NFiles;
        public uint PGid;
        public uint PJobc;
        public uint ETDev;
        public uint ETPGid;
        public int Nice;
        public ulong StartTvSec;
        public ulong StartTvUsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcTaskInfo
    {
        public ulong VirtualSize;
        public ulong ResidentSize;
        public ulong TotalUser;
        public ulong TotalSystem;
        public ulong ThreadsUser;
        public ulong ThreadsSystem;
        public int Policy;
        public int Faults;
        public int Pageins;
        public int CowFaults;
        public int MessagesSent;
        public int MessagesReceived;
        public int SyscallsMach;
        public int SyscallsUnix;
        public int Csw;
        public int ThreadNum;
        public int NumRunning;
        public int Priority;
    }

    [LibraryImport(LibSystem, EntryPoint = "proc_listallpids", SetLastError = true)]
    public static partial int ProcListAllPids(int* buffer, int bufferSize);

    [LibraryImport(LibSystem, EntryPoint = "proc_pidinfo", SetLastError = true)]
    public static partial int ProcPidInfo(int pid, int flavor, ulong arg, void* buffer, int bufferSize);

    [LibraryImport(LibSystem, EntryPoint = "proc_pidpath", SetLastError = true)]
    public static partial int ProcPidPath(int pid, byte* buffer, uint bufferSize);

    [LibraryImport(LibSystem, EntryPoint = "kill", SetLastError = true)]
    public static partial int Kill(int pid, int signal);

    [LibraryImport(LibSystem, EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int SysctlByName(string name, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    [LibraryImport(LibSystem, EntryPoint = "setxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial int SetXattr(string path, string name, byte* value, nuint size, uint position, int options);

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    public static partial int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account,
        uint* passwordLength, void** passwordData, nint* itemRef);

    [LibraryImport(Security, EntryPoint = "SecKeychainAddGenericPassword")]
    public static partial int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account,
        uint passwordLength, byte* passwordData, nint* itemRef);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    public static partial int SecKeychainItemModifyAttributesAndData(nint itemRef, nint attrList, uint length, byte* data);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemDelete")]
    public static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport(Security, EntryPoint = "SecKeychainItemFreeContent")]
    public static partial int SecKeychainItemFreeContent(nint attrList, void* data);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    public static partial void CFRelease(nint cf);

    // ---------------- helpers ----------------

    public static string? SysctlString(string name)
    {
        nuint len = 0;
        if (SysctlByName(name, null, &len, null, 0) != 0 || len == 0 || len > 4096) return null;
        byte* buf = stackalloc byte[(int)len];
        if (SysctlByName(name, buf, &len, null, 0) != 0) return null;
        return Encoding.UTF8.GetString(buf, (int)len).TrimEnd('\0').Trim();
    }

    public static ulong? SysctlUInt64(string name)
    {
        ulong value = 0;
        nuint len = (nuint)sizeof(ulong);
        return SysctlByName(name, &value, &len, null, 0) == 0 ? value : null;
    }

    public static bool TryGetBsdInfo(int pid, out ProcBsdInfo info)
    {
        ProcBsdInfo i = default;
        int n = ProcPidInfo(pid, ProcPidTbsdInfo, 0, &i, sizeof(ProcBsdInfo));
        info = i;
        return n == sizeof(ProcBsdInfo);
    }

    public static ulong ResidentBytes(int pid)
    {
        ProcTaskInfo t = default;
        return ProcPidInfo(pid, ProcPidTaskInfo, 0, &t, sizeof(ProcTaskInfo)) == sizeof(ProcTaskInfo) ? t.ResidentSize : 0;
    }

    public static string? PidPath(int pid)
    {
        byte* buf = stackalloc byte[4096];
        int n = ProcPidPath(pid, buf, 4096);
        return n > 0 ? Encoding.UTF8.GetString(buf, n) : null;
    }

    /// <summary>Process start time as a UTC FILETIME, matching what Windows reports (so identity checks work the same).</summary>
    public static long StartFileTime(in ProcBsdInfo i)
    {
        try
        {
            var start = DateTimeOffset.FromUnixTimeSeconds((long)i.StartTvSec).AddTicks((long)i.StartTvUsec * 10);
            return start.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    /// <summary>The process name: pbi_name (32 bytes) when set, else pbi_comm (16, truncated).</summary>
    public static string CommName(in ProcBsdInfo i)
    {
        fixed (byte* n = i.Name)
        {
            int len = 0;
            while (len < 32 && n[len] != 0) len++;
            if (len > 0) return Encoding.UTF8.GetString(n, len);
        }

        fixed (byte* p = i.Comm)
        {
            int len = 0;
            while (len < 16 && p[len] != 0) len++;
            return Encoding.UTF8.GetString(p, len);
        }
    }

    public const uint SZomb = 5;

    [LibraryImport(LibSystem, EntryPoint = "geteuid")]
    public static partial uint GetEuid();
}
