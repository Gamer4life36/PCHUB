using PcHub.Platform;
using PcHub.Services;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcHub.Platform.Windows;



/// <summary>
/// Drive space, SSD/HDD type and fragmentation for every fixed drive, readable without admin rights:
/// fragmentation comes from the statistics Windows' own "Optimize Drives" saves after each run
/// (HKLM\SOFTWARE\Microsoft\Dfrg\Statistics), and the drive type from the storage driver's
/// seek-penalty flag.
/// </summary>
public static unsafe partial class DriveService
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathNames(string volumeName, char* buffer, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    public static List<DriveStatus> Read()
    {
        var stats = ReadDefragStats();
        var list = new List<DriveStatus>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string letter = d.Name[..2]; // "C:"
                stats.TryGetValue(letter, out var s);
                list.Add(new DriveStatus(
                    letter,
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel,
                    QueryKind(letter),
                    d.TotalSize,
                    d.TotalFreeSpace,
                    s.Has ? s.Percent : null,
                    s.When));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that vanished or can't be read is left out of this refresh.
            }
        }

        return list;
    }

    /// <summary>Opens Windows' own Optimize Drives tool (it asks for admin itself when it needs it).</summary>
    public static void OpenOptimizeDrives() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dfrgui.exe") { UseShellExecute = true })?.Dispose();

    private static Dictionary<string, (bool Has, int Percent, DateTime? When)> ReadDefragStats()
    {
        var result = new Dictionary<string, (bool, int, DateTime?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Dfrg\Statistics");
            if (root is null) return result;
            foreach (string sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key is null) continue;
                DateTime? when = ParseSystemTime(key.GetValue("LastRunTime") as byte[]);
                if (when is null) continue; // never analysed

                string? letter = LetterForVolume(sub);
                if (letter is null) continue;
                int pct = key.GetValue("LastRunPercentFragmentation") is int p ? Math.Clamp(p, 0, 100) : 0;
                result[letter] = (true, pct, when);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Some locked-down PCs hide these statistics; drives then show "not analysed yet".
        }

        return result;
    }

    private static string? LetterForVolume(string subKey)
    {
        // Sub-keys look like "Volume{guid}".
        if (!subKey.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)) return null;
        char* buf = stackalloc char[512];
        if (!GetVolumePathNames($@"\\?\{subKey}\", buf, 512, out _)) return null;
        string first = new string(buf); // first null-terminated path, e.g. "C:\"
        return first.Length >= 2 && first[1] == ':' ? first[..2].ToUpperInvariant() : null;
    }

    private static DateTime? ParseSystemTime(byte[]? b)
    {
        if (b is null || b.Length < 16) return null;
        int year = BitConverter.ToUInt16(b, 0), month = BitConverter.ToUInt16(b, 2), day = BitConverter.ToUInt16(b, 6);
        int hour = BitConverter.ToUInt16(b, 8), minute = BitConverter.ToUInt16(b, 10), second = BitConverter.ToUInt16(b, 12);
        if (year < 2000 || month is < 1 or > 12 || day is < 1 or > 31 || hour > 23 || minute > 59 || second > 59) return null;
        try
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>SSDs report no seek penalty. Needs no access rights on the volume handle.</summary>
    private static DriveKind QueryKind(string letter)
    {
        nint h = CreateFile($@"\\.\{letter}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (h == -1 || h == 0) return DriveKind.Unknown;
        try
        {
            var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty };
            SeekPenaltyDescriptor desc;
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, &query, (uint)sizeof(StoragePropertyQuery), &desc, (uint)sizeof(SeekPenaltyDescriptor), out _, 0))
            {
                return DriveKind.Unknown;
            }

            return desc.IncursSeekPenalty != 0 ? DriveKind.Hdd : DriveKind.Ssd;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
