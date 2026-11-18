namespace PcHub.Services;

/// <summary>
/// Guards that keep PCHUB from claiming or ending processes it has no business touching.
/// </summary>
public static class PathSafety
{
    private static readonly string Windows = Norm(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>Process names PCHUB never ends, whatever a tool's settings say.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss.exe", "wininit.exe", "winlogon.exe", "lsass.exe", "services.exe", "smss.exe", "svchost.exe",
        "explorer.exe", "dwm.exe", "sihost.exe", "fontdrvhost.exe", "ctfmon.exe", "taskmgr.exe",
    };

    /// <summary>Programs a tool may open (browsers, shells) that must not be counted as part of it.</summary>
    private static readonly HashSet<string> NeverInherited = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe", "iexplore.exe",
        "explorer.exe", "notepad.exe", "WindowsTerminal.exe", "code.exe",
    };

    /// <summary>
    /// Folders too general to mean "this one app": drive roots, the user's main folders, Program Files
    /// roots and anything inside Windows. A tool must not claim every program under these.
    /// </summary>
    public static bool IsTooBroad(string folder)
    {
        string f = Norm(folder);
        if (f.Length <= 3) return true; // "C:" or "C:\"
        if (IsUnder(f, Windows) || f.Equals(Windows, StringComparison.OrdinalIgnoreCase)) return true;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] exact =
        [
            home,
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            Path.Combine(home, "AppData"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),
        ];
        foreach (string e in exact)
        {
            if (!string.IsNullOrEmpty(e) && Norm(e).Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>True for processes PCHUB must never end: itself, critical Windows processes, anything in Windows' folder.</summary>
    public static bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors)
    {
        if (selfAndAncestors.Contains(p.Pid)) return true;
        if (Protected.Contains(p.Name)) return true;
        if (p.Path is { } path && IsUnder(Norm(path), Windows)) return true;
        return false;
    }

    /// <summary>Whether a child process may be counted as part of the tool that started it.</summary>
    public static bool CanInherit(ProcInfo p)
    {
        if (NeverInherited.Contains(p.Name) || Protected.Contains(p.Name)) return false;
        // Console helpers (cmd, conhost) from Windows are fine to inherit; other Windows programs are not.
        if (p.Path is { } path && IsUnder(Norm(path), Windows))
        {
            return p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                   p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && path[folder.Length] == '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    public static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return p.TrimEnd('\\'); }
    }
}
