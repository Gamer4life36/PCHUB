using System.Globalization;

namespace PcHub.ViewModels;

public static class Format
{
    private const double Gb = 1024d * 1024 * 1024;
    private const double Mb = 1024d * 1024;

    public static string Bytes(double bytes) =>
        bytes >= Gb ? (bytes / Gb).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
                    : (bytes / Mb).ToString("0", CultureInfo.CurrentCulture) + " MB";

    public static string Gigs(double bytes) => (bytes / Gb).ToString("0.0", CultureInfo.CurrentCulture);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(0, (int)t.TotalSeconds)}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours < 24) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }
}
