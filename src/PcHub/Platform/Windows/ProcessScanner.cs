using PcHub.Platform;
using PcHub.Services;

namespace PcHub.Platform.Windows;


/// <summary>
/// Lists processes with one Toolhelp snapshot (no per-process Process objects) and caches
/// each process's path and start time, so only new processes cost a handle open.
/// </summary>
public sealed unsafe class ProcessScanner
{
    private Dictionary<int, ProcInfo> _cache = new();
    private long _scanCount;

    public IReadOnlyDictionary<int, ProcInfo> Scan()
    {
        _scanCount++;
        nint snap = Win32.CreateToolhelp32Snapshot(Win32.Th32csSnapProcess, 0);
        if (snap == Win32.InvalidHandle || snap == 0)
        {
            return _cache;
        }

        var next = new Dictionary<int, ProcInfo>(_cache.Count + 16);
        try
        {
            Win32.ProcessEntry32 entry = default;
            entry.Size = (uint)sizeof(Win32.ProcessEntry32);
            if (!Win32.Process32First(snap, &entry))
            {
                return _cache;
            }

            do
            {
                int pid = (int)entry.ProcessId;
                int ppid = (int)entry.ParentProcessId;
                if (pid <= 4)
                {
                    continue;
                }

                // Reuse cached details, but retry processes whose path couldn't be read every 10th scan.
                if (_cache.TryGetValue(pid, out var known) && known.ParentPid == ppid && NameEquals(known.Name, entry.ExeFile) &&
                    (known.Path is not null || _scanCount % 10 != 0))
                {
                    next[pid] = known;
                    continue;
                }

                string name = new string(entry.ExeFile);
                var (path, created) = QueryProcess(pid);
                next[pid] = new ProcInfo(pid, ppid, name, path, created);
            }
            while (Win32.Process32Next(snap, &entry));
        }
        finally
        {
            Win32.CloseHandle(snap);
        }

        _cache = next;
        return next;
    }

    /// <summary>Full image path and creation time, or (null, 0) when access is denied.</summary>
    public static (string? Path, long CreateTime) QueryProcess(int pid)
    {
        nint h = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == 0)
        {
            return (null, 0);
        }

        try
        {
            string? path = null;
            char* buf = stackalloc char[1024];
            uint size = 1024;
            if (Win32.QueryFullProcessImageName(h, 0, buf, ref size))
            {
                path = new string(buf, 0, (int)size);
            }

            long created = Win32.GetProcessTimes(h, out long c, out _, out _, out _) ? c : 0;
            return (path, created);
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private static bool NameEquals(string known, char* exeFile)
    {
        var span = new ReadOnlySpan<char>(exeFile, 260);
        int end = span.IndexOf('\0');
        if (end >= 0) span = span[..end];
        return span.SequenceEqual(known);
    }
}
