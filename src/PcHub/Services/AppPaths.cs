namespace PcHub.Services;

/// <summary>
/// Where settings, tools and history live:
///  1. Portable mode: a file named "portable" next to PCHUB.exe → data\ beside the exe (USB sticks, other drives).
///  2. Dev builds (running inside the source tree) → &lt;repo&gt;\data.
///  3. Otherwise the standard per-user folder: %LOCALAPPDATA%\PCHUB on Windows,
///     ~/Library/Application Support/PCHUB on macOS, ~/.local/share/PCHUB (XDG_DATA_HOME) on Linux.
/// </summary>
public static class AppPaths
{
    public static string Data { get; } = Ensure(FindDataDir());
    public static string Logs { get; } = Ensure(Path.Combine(Data, "logs"));
    public static string ToolsFile => Path.Combine(Data, "tools.json");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string ActivityFile => Path.Combine(Data, "activity.jsonl");
    public static string CrashFile => Path.Combine(Data, "crash.log");
    public static bool IsPortable { get; private set; }

    public static string ToolLog(string toolId) => Path.Combine(Logs, toolId + ".log");

    private static string FindDataDir()
    {
        string exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable")) || File.Exists(Path.Combine(exeDir, "portable.txt")))
        {
            IsPortable = true;
            return Path.Combine(exeDir, "data");
        }

        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; dir is not null && i < 8; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PCHUB.slnx")))
            {
                IsPortable = true;
                return Path.Combine(dir.FullName, "data");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            // The standard place for app data on macOS.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "PCHUB");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCHUB");
    }

    /// <summary>
    /// When running inside a macOS .app bundle, AppContext.BaseDirectory is Contents/MacOS; portable mode and the
    /// dev-tree search still work from there.
    /// </summary>
    public static string DisplayLocation => IsPortable ? $"Portable · {Data}" : Data;

    /// <summary>Creates the folder; if that is impossible (read-only USB stick, locked profile) uses %TEMP%\PCHUB instead.</summary>
    private static string Ensure(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string fallback = Path.Combine(Path.GetTempPath(), "PCHUB", Path.GetFileName(path));
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
