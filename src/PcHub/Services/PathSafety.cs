using PcHub.Platform;

namespace PcHub.Services;

/// <summary>Path helpers shared by both platforms. The rules about what is too broad or protected live in each platform.</summary>
public static class PathSafety
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    public static bool IsTooBroad(string folder) => Os.Current.IsTooBroad(folder);
    public static bool IsProtected(ProcInfo p, IReadOnlySet<int> selfAndAncestors) => Os.Current.IsProtected(p, selfAndAncestors);
    public static bool CanInherit(ProcInfo p) => Os.Current.CanInherit(p);

    /// <summary>True when path is strictly inside folder (a whole path segment, not just a matching prefix).</summary>
    public static bool IsUnder(string path, string folder) =>
        path.Length > folder.Length && (path[folder.Length] == Sep || path[folder.Length] == '/') &&
        path.StartsWith(folder, Os.Current.PathComparison);

    /// <summary>Full path without a trailing separator (except a bare root such as "/" or "C:\").</summary>
    public static string Norm(string p)
    {
        string full;
        try { full = Path.GetFullPath(p); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { full = p; }

        string trimmed = full.TrimEnd(Sep, '/');
        return trimmed.Length == 0 ? full[..1] : trimmed;
    }
}
