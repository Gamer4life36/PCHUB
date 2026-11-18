using PcHub.Platform;

namespace PcHub.Services;

/// <summary>
/// Finds common local-AI apps: the platform's fixed install locations, plus a shallow (2-level) look through
/// the drives / home folder for known folder layouts. Works on Windows and macOS. It only reads directory
/// listings; nothing is started or changed.
/// </summary>
public static class ToolDetector
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "$WinREAgent",
        "System Volume Information", "Recovery", "PerfLogs", "Users", "Config.Msi", "MSOCache",
        "AppData", "OneDriveTemp", "$SysReset", "Intel", "AMD", "NVIDIA",
        // macOS
        "Library", "System", "Applications", "Volumes", "private", "usr", "bin", "sbin", "cores", "dev", "etc", "opt", "tmp", "var",
        ".Trash", "Movies", "Music", "Pictures", "Public",
        // both
        "node_modules", ".git", ".cache", ".npm",
    };

    public static List<ToolDefinition> Detect(IReadOnlyCollection<string>? excludes = null)
    {
        var os = Os.Current;
        var found = new List<ToolDefinition>();
        var excluded = (excludes ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => PathSafety.Norm(Environment.ExpandEnvironmentVariables(e)))
            .ToList();

        os.DetectFixedApps(found);

        foreach (string dir in CandidateFolders(os, excluded))
        {
            try
            {
                DetectInFolder(os, dir, found);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Unreadable folders are simply not candidates.
            }
        }

        return Dedupe(found);
    }

    private static void DetectInFolder(IPlatform os, string d, List<ToolDefinition> found)
    {
        // A general folder (Downloads, a drive root, the home folder) can't be claimed as one app's folder;
        // only single-file apps found there are added, matched by their exact file.
        if (os.IsTooBroad(d))
        {
            DetectKobold(os, d, found);
            return;
        }

        bool Has(string rel) => File.Exists(Path.Combine(d, rel));
        bool HasDir(string rel) => Directory.Exists(Path.Combine(d, rel));

        // ComfyUI portable (Windows zip release)
        if (os.IsWindows && Has(Path.Combine("python_embeded", "python.exe")) && Has(Path.Combine("ComfyUI", "main.py")))
        {
            found.Add(new ToolDefinition
            {
                Id = "comfyui", Name = "ComfyUI",
                FileName = Path.Combine(d, "python_embeded", "python.exe"),
                Arguments = @"-s ComfyUI\main.py --windows-standalone-build",
                WorkingDirectory = d, Hidden = true, MatchPaths = [d],
                Port = 8188, Url = "http://127.0.0.1:8188",
            });
            return;
        }

        // ComfyUI from git with its own venv
        if (Has("main.py") && Has("nodes.py") && HasDir("comfy"))
        {
            if (os.VenvPython(d) is { } py)
            {
                found.Add(new ToolDefinition
                {
                    Id = "comfyui", Name = "ComfyUI", FileName = py, Arguments = "main.py",
                    WorkingDirectory = d, Hidden = true, MatchPaths = [d],
                    Port = 8188, Url = "http://127.0.0.1:8188",
                });
            }

            return;
        }

        // AUTOMATIC1111 / Forge / reForge
        if (Has("webui.py") && (Has("webui-user.bat") || Has("webui.sh")))
        {
            string name = HasDir("modules_forge") ? "Forge" : "Stable Diffusion WebUI";
            found.Add(ScriptTool(os, name, d, os.IsWindows ? "webui-user.bat" : "webui.sh", 7860));
            return;
        }

        if (HasDir("Fooocus") && Has(Path.Combine("Fooocus", "entry_with_update.py")))
        {
            if (os.IsWindows && Has("run.bat"))
            {
                found.Add(ScriptTool(os, "Fooocus", d, "run.bat", 7865));
            }
            else if (os.VenvPython(Path.Combine(d, "Fooocus")) is { } fpy)
            {
                found.Add(new ToolDefinition
                {
                    Id = "fooocus", Name = "Fooocus", FileName = fpy, Arguments = "entry_with_update.py",
                    WorkingDirectory = Path.Combine(d, "Fooocus"), Hidden = true, MatchPaths = [d],
                    Port = 7865, Url = "http://127.0.0.1:7865",
                });
            }

            return;
        }

        if (HasDir("src") && Has(Path.Combine("src", "SwarmUI.csproj")))
        {
            string script = os.IsWindows ? "launch-windows.bat" : "launch-macos.sh";
            if (Has(script)) found.Add(ScriptTool(os, "SwarmUI", d, script, 7801));
            return;
        }

        if (Has("server.py") && HasDir("modules"))
        {
            string script = os.IsWindows ? "start_windows.bat" : "start_macos.sh";
            if (Has(script)) found.Add(ScriptTool(os, "Text Generation WebUI", d, script, 7860));
            return;
        }

        string? invoke = os.FindExecutable(Path.Combine(d, ".venv"), "invokeai-web") ?? os.FindExecutable(Path.Combine(d, "venv"), "invokeai-web");
        if (invoke is not null)
        {
            found.Add(new ToolDefinition
            {
                Id = "invokeai", Name = "InvokeAI", FileName = invoke, WorkingDirectory = d, Hidden = true,
                MatchPaths = [d], Port = 9090, Url = "http://127.0.0.1:9090",
            });
            return;
        }

        DetectKobold(os, d, found);
    }

    /// <summary>KoboldCpp is a single file that often sits in Downloads, so match the file, not the folder.</summary>
    private static void DetectKobold(IPlatform os, string d, List<ToolDefinition> found)
    {
        string pattern = os.IsWindows ? "koboldcpp*.exe" : "koboldcpp-mac*";
        foreach (string exe in Directory.EnumerateFiles(d, pattern))
        {
            found.Add(new ToolDefinition
            {
                Id = "koboldcpp", Name = "KoboldCpp", FileName = exe, WorkingDirectory = d, Hidden = !os.IsWindows,
                MatchFiles = [exe], Port = 5001, Url = "http://127.0.0.1:5001",
            });
        }
    }

    /// <summary>A tool started by a script (.bat on Windows, .sh on macOS).</summary>
    private static ToolDefinition ScriptTool(IPlatform os, string name, string dir, string script, int port) => new()
    {
        Id = Slug(name),
        Name = name,
        FileName = Path.Combine(dir, script),
        WorkingDirectory = dir,
        // macOS has no console window to show, so script output always goes to a log there.
        Hidden = !os.IsWindows,
        MatchPaths = [dir],
        Port = port,
        Url = $"http://127.0.0.1:{port}",
    };

    /// <summary>The platform's scan roots and their sub-folders, two levels deep.</summary>
    private static IEnumerable<string> CandidateFolders(IPlatform os, List<string> excluded)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in os.ScanRoots())
        {
            if (IsExcluded(root, excluded) || !seen.Add(root)) continue;
            yield return root;
            foreach (string l1 in SafeDirs(l1Parent: root))
            {
                if (IsExcluded(l1, excluded) || !seen.Add(l1)) continue;
                yield return l1;
                foreach (string l2 in SafeDirs(l1))
                {
                    if (IsExcluded(l2, excluded) || !seen.Add(l2)) continue;
                    yield return l2;
                }
            }
        }
    }

    private static bool IsExcluded(string dir, List<string> excluded)
    {
        string d = PathSafety.Norm(dir);
        foreach (string e in excluded)
        {
            if (d.Equals(e, StringComparison.OrdinalIgnoreCase) || PathSafety.IsUnder(d, e)) return true;
        }

        return false;
    }

    private static IEnumerable<string> SafeDirs(string l1Parent)
    {
        DirectoryInfo[] dirs;
        try
        {
            dirs = new DirectoryInfo(l1Parent).GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var di in dirs)
        {
            if (SkipNames.Contains(di.Name) || di.Name.StartsWith('$') || di.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
                (di.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            yield return di.FullName;
        }
    }

    private static List<ToolDefinition> Dedupe(List<ToolDefinition> tools)
    {
        var result = new List<ToolDefinition>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameCount = tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var t in tools)
        {
            string key = t.FileName + "|" + t.Arguments + "|" + string.Join(';', t.MatchPaths) + "|" + string.Join(';', t.MatchFiles);
            if (!keys.Add(key)) continue;

            if (nameCount[t.Name] > 1)
            {
                // Two installs of the same app: tell them apart by folder name (full path is in the tooltip).
                string wd = PathSafety.Norm(t.WorkingDirectory ?? "");
                string folder = Path.GetFileName(wd);
                string parent = Path.GetFileName(Path.GetDirectoryName(wd) ?? "");
                string tag = folder.Equals(t.Name, StringComparison.OrdinalIgnoreCase) ? parent : folder;
                if (!string.IsNullOrEmpty(tag)) t.Name = $"{t.Name} ({tag})";
            }

            string id = t.Id;
            for (int n = 2; !ids.Add(id); n++) id = $"{t.Id}-{n}";
            t.Id = id;
            result.Add(t);
        }

        return result;
    }

    public static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        string s = new string(chars).Trim('-');
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Length == 0 ? "app" : s;
    }
}
