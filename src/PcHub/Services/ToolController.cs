using System.Diagnostics;
using PcHub.Platform;

namespace PcHub.Services;

public sealed record StopResult(int Closed, int Killed, IReadOnlyList<string> Errors, IReadOnlyList<string> UnloadedModels);

/// <summary>Starts and stops tools. Only ever touches processes the monitor attributed to that tool.</summary>
public static class ToolController
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Starts the tool. Hidden tools write their output to a log file the launching shell owns, so closing
    /// PCHUB never breaks the tool's output.
    /// </summary>
    public static Process Start(ToolDefinition t)
    {
        if (string.IsNullOrWhiteSpace(t.FileName))
        {
            throw new InvalidOperationException($"{t.Name} has no program set.");
        }

        bool isBareName = !t.FileName.Contains('\\') && !t.FileName.Contains('/');
        bool isBundle = t.FileName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(t.FileName);
        if (!isBareName && !isBundle && !File.Exists(t.FileName))
        {
            throw new FileNotFoundException($"Can't find {t.FileName}. Was {t.Name} moved or uninstalled?");
        }

        string? workDir = t.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            workDir = isBareName ? Environment.CurrentDirectory : Path.GetDirectoryName(t.FileName) ?? Environment.CurrentDirectory;
        }

        string? log = null;
        if (t.Hidden)
        {
            log = AppPaths.ToolLog(t.Id);
            RotateLog(log);
            File.AppendAllText(log, $"{Environment.NewLine}===== {t.Name} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
        }

        var psi = Os.Current.BuildStart(t, workDir, log);
        if (!psi.UseShellExecute)
        {
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            if (log is not null) psi.Environment["NO_COLOR"] = "1"; // plain-text logs

            // Tools that "open a browser" when they start (ComfyUI --auto-launch, Gradio apps like Forge and
            // Fooocus) use Python's webbrowser module, which honours BROWSER. Pointing it at PCHUB means those
            // pages open in a PCHUB tab instead of a browser tab the user didn't ask for.
            if (Environment.ProcessPath is { } self) psi.Environment["BROWSER"] = self;
        }

        return Process.Start(psi) ?? throw new InvalidOperationException($"The system did not start {t.Name}.");
    }

    /// <summary>
    /// Ends exactly the processes the monitor matched to this tool, nothing else (no whole-tree kills, so a
    /// browser or another tool it started is left alone). Ollama models are unloaded first. Off the UI thread.
    /// </summary>
    public static async Task<StopResult> StopAsync(ToolDefinition t, IReadOnlyList<ProcInfo> procs, bool unloadOnly = false)
    {
        var errors = new List<string>();
        var unloaded = new List<string>();

        if (t.IsOllama && t.Port is > 0)
        {
            try
            {
                unloaded.AddRange(await OllamaClient.UnloadAllAsync(t.Port.Value).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
            {
                errors.Add("Ollama didn't answer the unload request: " + ex.Message);
            }

            if (unloadOnly) return new StopResult(0, 0, errors, unloaded);
        }

        var ended = await Os.Current.EndProcessesAsync(procs, t.GracefulStopSeconds).ConfigureAwait(false);
        errors.AddRange(ended.Errors);
        return new StopResult(ended.Closed, ended.Killed, errors, unloaded);
    }

    private static void RotateLog(string log)
    {
        try
        {
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > MaxLogBytes) File.Move(log, log + ".old", overwrite: true);
        }
        catch (IOException)
        {
            // Log in use; keep appending.
        }
    }
}
