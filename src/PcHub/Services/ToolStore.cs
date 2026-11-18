using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHub.Services;

/// <summary>One app the hub can watch, start and stop. Stored in data\tools.json (hand-editable).</summary>
public sealed class ToolDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"App" (default) or "Ollama" (frees VRAM by unloading models instead of closing).</summary>
    public string Kind { get; set; } = "App";

    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string? WorkingDirectory { get; set; }

    /// <summary>Run with no window; output goes to data\logs\&lt;id&gt;.log.</summary>
    public bool Hidden { get; set; }

    /// <summary>A process belongs to this tool if its exe is inside one of these folders (or its parent's is).</summary>
    public List<string> MatchPaths { get; set; } = new();

    /// <summary>Exact exe files that belong to this tool (for apps that sit in shared folders like Downloads).</summary>
    public List<string> MatchFiles { get; set; } = new();

    public int? Port { get; set; }
    public string? Url { get; set; }
    public bool IncludeInFreeGpu { get; set; } = true;
    public int GracefulStopSeconds { get; set; } = 8;

    [JsonIgnore] public bool IsOllama => string.Equals(Kind, "Ollama", StringComparison.OrdinalIgnoreCase);
}

public sealed class ToolsFile
{
    public List<ToolDefinition> Tools { get; set; } = new();
}

public sealed class AppSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }

    /// <summary>Which GPU to watch ("luid_..."), or null for the one with the most VRAM.</summary>
    public string? AdapterLuid { get; set; }

    /// <summary>Sample less often to save CPU on older machines.</summary>
    public bool LowPower { get; set; }

    /// <summary>Where "Get apps" saves downloads; null = Documents\PCHUB Downloads.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>The user confirmed they are 18+ and wants NSFW (unfiltered) apps listed.</summary>
    public bool ShowAdultApps { get; set; }

    /// <summary>Opt-in: after each download, ask Claude whether the app has an age gate.</summary>
    public bool ClaudeAgeCheck { get; set; }

    /// <summary>The user's Anthropic API key, DPAPI-encrypted (see ClaudeKey).</summary>
    public string? ClaudeKeyProtected { get; set; }

    /// <summary>Folders the app scanner must never look inside.</summary>
    public List<string> ScanExcludes { get; set; } = new();
}

public sealed class ActivityRecord
{
    public DateTime Time { get; set; }
    public string Tool { get; set; } = "";
    public string Level { get; set; } = "Info";
    public string Message { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ToolsFile))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class HubJson : JsonSerializerContext;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(ActivityRecord))]
internal sealed partial class LineJson : JsonSerializerContext;

public static class ToolStore
{
    /// <summary>Loads tools.json, creating it from what is installed on this PC the first time.</summary>
    public static (List<ToolDefinition> Tools, List<string> Problems) Load(IReadOnlyCollection<string> scanExcludes)
    {
        var problems = new List<string>();
        string file = AppPaths.ToolsFile;
        if (!File.Exists(file))
        {
            // Start empty: PCHUB works on its own, and the user chooses which apps to add.
            var defaults = new ToolsFile();
            WriteAtomic(file, JsonSerializer.Serialize(defaults, HubJson.Default.ToolsFile));
        }

        ToolsFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(File.ReadAllText(file), HubJson.Default.ToolsFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"tools.json has an error (line {ex.LineNumber + 1}): {ex.Message}", ex);
        }

        var tools = new List<ToolDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in parsed?.Tools ?? [])
        {
            if (t is null) continue;
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
            {
                problems.Add("A tool in tools.json has no Id or Name and was skipped.");
                continue;
            }

            if (!ids.Add(t.Id))
            {
                problems.Add($"Duplicate tool id '{t.Id}' skipped.");
                continue;
            }

            var paths = new List<string>();
            foreach (string? raw in t.MatchPaths ?? [])
            {
                string? full = Rooted(raw);
                if (full is null)
                {
                    if (!string.IsNullOrWhiteSpace(raw)) problems.Add($"'{t.Name}': ignored MatchPath '{raw}' (needs a full folder path like D:\\Apps\\Tool).");
                    continue;
                }

                if (PathSafety.IsTooBroad(full))
                {
                    problems.Add($"'{t.Name}': ignored MatchPath '{full}' because it is too general and would claim unrelated programs. Use the app's own folder.");
                    continue;
                }

                paths.Add(full.TrimEnd('\\'));
            }

            var files = new List<string>();
            foreach (string? raw in t.MatchFiles ?? [])
            {
                string? full = Rooted(raw);
                if (full is not null) files.Add(full);
            }

            t.MatchPaths = paths;
            t.MatchFiles = files;
            if (t.MatchPaths.Count == 0 && t.MatchFiles.Count == 0)
            {
                problems.Add($"'{t.Name}' has no usable MatchPaths or MatchFiles, so PCHUB can't see when it runs.");
            }

            t.FileName = Environment.ExpandEnvironmentVariables(t.FileName ?? "");
            t.Arguments ??= "";
            if (t.WorkingDirectory is not null)
            {
                t.WorkingDirectory = Environment.ExpandEnvironmentVariables(t.WorkingDirectory);
            }

            t.GracefulStopSeconds = Math.Clamp(t.GracefulStopSeconds, 0, 60);
            tools.Add(t);
        }

        return (tools, problems);
    }

    /// <summary>Expands and normalizes a full path; null for blanks, relative paths or invalid characters.</summary>
    private static string? Rooted(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        try
        {
            return Path.IsPathFullyQualified(expanded) ? Path.GetFullPath(expanded) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), HubJson.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            CrashLog.Write("settings.json unreadable, using defaults", ex);
        }

        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s) =>
        WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(s, HubJson.Default.AppSettings));

    /// <summary>Reads the raw file (unexpanded), so edits made from the UI keep the user's own text.</summary>
    public static List<ToolDefinition> LoadRaw()
    {
        if (!File.Exists(AppPaths.ToolsFile))
        {
            return new List<ToolDefinition>();
        }

        return JsonSerializer.Deserialize(File.ReadAllText(AppPaths.ToolsFile), HubJson.Default.ToolsFile)?.Tools ?? new List<ToolDefinition>();
    }

    public static void SaveRaw(List<ToolDefinition> tools) =>
        WriteAtomic(AppPaths.ToolsFile, JsonSerializer.Serialize(new ToolsFile { Tools = tools }, HubJson.Default.ToolsFile));

    /// <summary>Write to a temp file then swap, so a crash mid-write never leaves a half file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
