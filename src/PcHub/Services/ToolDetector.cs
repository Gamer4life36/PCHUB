namespace PcHub.Services;

/// <summary>
/// Finds common local-AI apps on any PC: fixed install locations plus a shallow (2-level) look
/// through each fixed drive and the user's Desktop/Documents/Downloads for known folder layouts.
/// It only reads directory listings; nothing is started or changed.
/// </summary>
public static class ToolDetector
{
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "$WinREAgent",
        "System Volume Information", "Recovery", "PerfLogs", "Users", "Config.Msi", "MSOCache",
        "node_modules", ".git", "AppData", "OneDriveTemp", "$SysReset", "Intel", "AMD", "NVIDIA",
    };

    private static readonly (string Exe, string Name)[] KnownDesktopApps =
    [
        ("LM Studio.exe", "LM Studio"),
        ("Jan.exe", "Jan"),
        ("ComfyUI.exe", "ComfyUI Desktop"),
        ("Invoke Community Edition.exe", "Invoke"),
        ("Pinokio.exe", "Pinokio"),
        ("AnythingLLM.exe", "AnythingLLM"),
        ("Msty.exe", "Msty"),
    ];

    public static List<ToolDefinition> Detect(IReadOnlyCollection<string>? excludes = null)
    {
        var found = new List<ToolDefinition>();
        var excluded = (excludes ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => Path.GetFullPath(Environment.ExpandEnvironmentVariables(e)).TrimEnd('\\'))
            .ToList();

        DetectFixedLocations(found);

        foreach (string dir in CandidateFolders(excluded))
        {
            try
            {
                DetectInFolder(dir, found);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Unreadable folders are simply not candidates.
            }
        }

        return Dedupe(found);
    }

    private static void DetectFixedLocations(List<ToolDefinition> found)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (string dir in new[] { Path.Combine(local, @"Programs\Ollama"), Path.Combine(pf, "Ollama") })
        {
            string exe = Path.Combine(dir, "ollama.exe");
            if (File.Exists(exe))
            {
                found.Add(new ToolDefinition
                {
                    Id = "ollama", Name = "Ollama", Kind = "Ollama",
                    FileName = exe, Arguments = "serve", WorkingDirectory = dir, Hidden = true,
                    MatchPaths = [dir], Port = 11434,
                });
                break;
            }
        }

        string programs = Path.Combine(local, "Programs");
        if (Directory.Exists(programs))
        {
            foreach (string sub in SafeDirs(programs))
            {
                foreach (var (exeName, name) in KnownDesktopApps)
                {
                    string exe = Path.Combine(sub, exeName);
                    if (!File.Exists(exe))
                    {
                        continue;
                    }

                    var t = new ToolDefinition
                    {
                        Id = Slug(name), Name = name, FileName = exe, WorkingDirectory = sub, MatchPaths = [sub],
                    };
                    if (name == "LM Studio")
                    {
                        // LM Studio runs its model engines from ~/.lmstudio.
                        string engines = Path.Combine(home, ".lmstudio");
                        if (Directory.Exists(engines)) t.MatchPaths.Add(engines);
                    }

                    found.Add(t);
                }
            }
        }

        foreach (string chat in new[] { Path.Combine(home, @"gpt4all\bin\chat.exe"), Path.Combine(pf, @"gpt4all\bin\chat.exe") })
        {
            if (File.Exists(chat))
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(chat))!;
                found.Add(new ToolDefinition { Id = "gpt4all", Name = "GPT4All", FileName = chat, WorkingDirectory = root, MatchPaths = [root] });
                break;
            }
        }
    }

    private static void DetectInFolder(string d, List<ToolDefinition> found)
    {
        // A general folder (Downloads, a drive root, the user folder) can't be claimed as one app's folder;
        // only single-exe apps found there are added, matched by their exact file.
        if (PathSafety.IsTooBroad(d))
        {
            DetectKobold(d, found);
            return;
        }

        bool Has(string rel) => File.Exists(Path.Combine(d, rel));
        bool HasDir(string rel) => Directory.Exists(Path.Combine(d, rel));

        // ComfyUI portable (Windows zip release)
        if (Has(@"python_embeded\python.exe") && Has(@"ComfyUI\main.py"))
        {
            found.Add(new ToolDefinition
            {
                Id = "comfyui", Name = "ComfyUI",
                FileName = Path.Combine(d, @"python_embeded\python.exe"),
                Arguments = @"-s ComfyUI\main.py --windows-standalone-build",
                WorkingDirectory = d, Hidden = true, MatchPaths = [d],
                Port = 8188, Url = "http://127.0.0.1:8188",
            });
            return;
        }

        // ComfyUI from git with its own venv
        if (Has("main.py") && Has("nodes.py") && HasDir("comfy"))
        {
            string? py = FirstExisting(d, @"venv\Scripts\python.exe", @".venv\Scripts\python.exe");
            if (py is not null)
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
        if (Has("webui-user.bat") && Has("webui.py"))
        {
            string name = HasDir("modules_forge") ? "Forge" : "Stable Diffusion WebUI";
            found.Add(BatchTool(name, d, "webui-user.bat", 7860));
            return;
        }

        if (HasDir("Fooocus") && Has(@"Fooocus\entry_with_update.py") && Has("run.bat"))
        {
            found.Add(BatchTool("Fooocus", d, "run.bat", 7865));
            return;
        }

        if (Has("launch-windows.bat") && HasDir("src") && Has(@"src\SwarmUI.csproj"))
        {
            found.Add(BatchTool("SwarmUI", d, "launch-windows.bat", 7801));
            return;
        }

        if (Has("start_windows.bat") && Has("server.py") && HasDir("modules"))
        {
            found.Add(BatchTool("Text Generation WebUI", d, "start_windows.bat", 7860));
            return;
        }

        string? invoke = FirstExisting(d, @".venv\Scripts\invokeai-web.exe", @"venv\Scripts\invokeai-web.exe");
        if (invoke is not null)
        {
            found.Add(new ToolDefinition
            {
                Id = "invokeai", Name = "InvokeAI", FileName = invoke, WorkingDirectory = d, Hidden = true,
                MatchPaths = [d], Port = 9090, Url = "http://127.0.0.1:9090",
            });
            return;
        }

        DetectKobold(d, found);
    }

    /// <summary>KoboldCpp is a single exe that often sits in Downloads, so match the file, not the folder.</summary>
    private static void DetectKobold(string d, List<ToolDefinition> found)
    {
        foreach (string exe in Directory.EnumerateFiles(d, "koboldcpp*.exe"))
        {
            found.Add(new ToolDefinition
            {
                Id = "koboldcpp", Name = "KoboldCpp", FileName = exe, WorkingDirectory = d,
                MatchFiles = [exe], Port = 5001, Url = "http://127.0.0.1:5001",
            });
        }
    }

    private static ToolDefinition BatchTool(string name, string dir, string bat, int port) => new()
    {
        Id = Slug(name),
        Name = name,
        FileName = Path.Combine(dir, bat),
        WorkingDirectory = dir,
        MatchPaths = [dir],
        Port = port,
        Url = $"http://127.0.0.1:{port}",
    };

    /// <summary>Each fixed drive and the user's main folders, two levels deep.</summary>
    private static IEnumerable<string> CandidateFolders(List<string> excluded)
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed && drive.IsReady) roots.Add(drive.RootDirectory.FullName);
            }
            catch (IOException) { /* drive went away */ }
        }

        foreach (var sf in new[] { Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            string p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) roots.Add(downloads);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (IsExcluded(root, excluded) || !seen.Add(root)) continue;
            yield return root;
            foreach (string l1 in SafeDirs(root))
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
        string d = dir.TrimEnd('\\');
        foreach (string e in excluded)
        {
            if (d.Equals(e, StringComparison.OrdinalIgnoreCase) ||
                (d.Length > e.Length && d[e.Length] == '\\' && d.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SafeDirs(string parent)
    {
        DirectoryInfo[] dirs;
        try
        {
            dirs = new DirectoryInfo(parent).GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var di in dirs)
        {
            if (SkipNames.Contains(di.Name) || di.Name.StartsWith('$') ||
                (di.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
            {
                continue;
            }

            yield return di.FullName;
        }
    }

    private static string? FirstExisting(string dir, params string[] rels)
    {
        foreach (string r in rels)
        {
            string p = Path.Combine(dir, r);
            if (File.Exists(p)) return p;
        }

        return null;
    }

    private static List<ToolDefinition> Dedupe(List<ToolDefinition> tools)
    {
        var result = new List<ToolDefinition>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nameCount = tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.Count());
        foreach (var t in tools)
        {
            string key = t.FileName + "|" + string.Join(';', t.MatchPaths) + "|" + string.Join(';', t.MatchFiles);
            if (!keys.Add(key)) continue;

            if (nameCount[t.Name] > 1)
            {
                // Two installs of the same app: tell them apart by folder name (full path is in the tooltip).
                string folder = Path.GetFileName((t.WorkingDirectory ?? "").TrimEnd('\\'));
                string parent = Path.GetFileName(Path.GetDirectoryName((t.WorkingDirectory ?? "").TrimEnd('\\')) ?? "");
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
