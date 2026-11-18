namespace PcHub.Services;

public static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string context, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(AppPaths.CrashFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Last-resort logger: if the disk write fails there is nowhere left to report it.
        }
    }
}
