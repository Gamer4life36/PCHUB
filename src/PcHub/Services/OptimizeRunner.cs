using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PcHub.Services;

/// <summary>
/// Runs Windows' own optimizer (defrag.exe) on one drive. It needs admin, so it is started with
/// "runas" (Windows shows its approval prompt) and writes progress to a log PCHUB can read.
/// </summary>
public static partial class OptimizeRunner
{
    [GeneratedRegex(@"([A-Za-z][A-Za-z ]+?):\s+(\d{1,3})% complete", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    public static string LogFor(string letter) => AppPaths.ToolLog("optimize-" + letter.TrimEnd(':').ToLowerInvariant());

    /// <summary>Starts the optimizer. Returns null if the user declined the admin prompt.</summary>
    public static Process? Start(string letter)
    {
        if (letter.Length != 2 || !char.IsLetter(letter[0]) || letter[1] != ':')
        {
            throw new ArgumentException("Not a drive letter: " + letter, nameof(letter));
        }

        string log = LogFor(letter);
        File.WriteAllText(log, "");
        string defrag = Path.Combine(Environment.SystemDirectory, "defrag.exe");
        // /O = the right optimization for the media type (defrag for HDD, retrim for SSD), /U = progress, /V = details.
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{defrag}\" {letter} /O /U /V > \"{log}\" 2>&1\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // ERROR_CANCELLED: the user said no to the admin prompt
        }
    }

    /// <summary>Latest stage and percent from the log, e.g. ("Defragmentation", 42).</summary>
    public static (string Stage, int Percent)? ReadProgress(string letter)
    {
        try
        {
            using var fs = new FileStream(LogFor(letter), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > 64 * 1024) fs.Seek(-64 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(fs);
            string text = reader.ReadToEnd();
            Match? last = null;
            foreach (Match m in ProgressLine().Matches(text)) last = m;
            return last is null ? null : (last.Groups[1].Value.Trim(), Math.Clamp(int.Parse(last.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), 0, 100));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The last meaningful line of the log, used when the run fails.</summary>
    public static string LastLine(string letter)
    {
        try
        {
            using var fs = new FileStream(LogFor(letter), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd()
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.Length > 3) ?? "";
        }
        catch (IOException)
        {
            return "";
        }
    }
}
