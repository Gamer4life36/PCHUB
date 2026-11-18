using System.Runtime.InteropServices;
using PcHub.Native;
using System.Net.Http;
using System.ComponentModel;
using System.Diagnostics;

namespace PcHub.Services;

public sealed record StopResult(int Closed, int Killed, IReadOnlyList<string> Errors, IReadOnlyList<string> UnloadedModels);

/// <summary>Starts and stops tools. Only ever touches processes the monitor attributed to that tool.</summary>
public static class ToolController
{
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Starts the tool. Hidden tools run through cmd.exe with output redirected to a log file the
    /// child owns, so closing the hub never breaks the tool's output pipe.
    /// </summary>
    public static Process Start(ToolDefinition t)
    {
        if (string.IsNullOrWhiteSpace(t.FileName))
        {
            throw new InvalidOperationException($"{t.Name} has no program set.");
        }

        bool isBareName = !t.FileName.Contains('\\') && !t.FileName.Contains('/');
        if (!isBareName && !File.Exists(t.FileName))
        {
            throw new FileNotFoundException($"Can't find {t.FileName}. Was {t.Name} moved or uninstalled?");
        }

        string? workDir = t.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            workDir = isBareName ? Environment.CurrentDirectory : Path.GetDirectoryName(t.FileName);
        }

        string ext = Path.GetExtension(t.FileName).ToLowerInvariant();
        ProcessStartInfo psi;
        if (t.Hidden)
        {
            string log = AppPaths.ToolLog(t.Id);
            RotateLog(log);
            File.AppendAllText(log, $"{Environment.NewLine}===== {t.Name} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
            string inner = ext is ".bat" or ".cmd"
                ? $"call \"{t.FileName}\" {t.Arguments}"
                : $"\"{t.FileName}\" {t.Arguments}";
            psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{inner} >> \"{log}\" 2>&1\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir,
            };
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["NO_COLOR"] = "1"; // plain-text logs, no terminal colour codes
        }
        else if (ext is ".bat" or ".cmd")
        {
            psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{t.FileName}\" {t.Arguments}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = workDir,
            };
        }
        else if (ext is ".exe" || isBareName)
        {
            psi = new ProcessStartInfo(t.FileName, t.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = workDir,
            };
        }
        else
        {
            // Anything else (documents, shortcuts) must go through the shell; it can't take extra settings.
            psi = new ProcessStartInfo(t.FileName, t.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = workDir,
            };
        }

        if (!psi.UseShellExecute && Environment.ProcessPath is { } self)
        {
            // Tools that "open a browser" when they start (ComfyUI --auto-launch, Gradio apps like Forge
            // and Fooocus) use Python's webbrowser module, which honours BROWSER. Pointing it at PCHUB
            // means those pages open in a PCHUB tab instead of a browser tab the user didn't ask for.
            psi.Environment["BROWSER"] = self;
        }

        return Process.Start(psi) ?? throw new InvalidOperationException($"Windows did not start {t.Name}.");
    }

    /// <summary>
    /// Ends exactly the processes the monitor matched to this tool, nothing else: no whole-tree kills,
    /// so a browser or another tool a process started is left alone. Each target is opened once and
    /// checked against its recorded start time; holding that handle means Windows can't hand the PID
    /// to another program while we wait. PCHUB itself and critical Windows processes are never ended.
    /// Windows are asked to close first so apps can save. Runs off the UI thread.
    /// </summary>
    public static Task<StopResult> StopAsync(ToolDefinition t, IReadOnlyList<ProcInfo> procs, bool unloadOnly = false) =>
        Task.Run(() => StopCoreAsync(t, procs, unloadOnly));

    private static async Task<StopResult> StopCoreAsync(ToolDefinition t, IReadOnlyList<ProcInfo> procs, bool unloadOnly)
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

            if (unloadOnly)
            {
                return new StopResult(0, 0, errors, unloaded);
            }
        }

        var self = SelfAndAncestors();
        var targets = new List<(ProcInfo Info, nint Handle)>();
        try
        {
            // Newest first: children are newer than parents, so helpers end before the processes that started them.
            foreach (var p in procs.OrderByDescending(p => p.CreateTime))
            {
                if (PathSafety.IsProtected(p, self)) continue;
                nint h = OpenVerified(p);
                if (h != 0) targets.Add((p, h));
            }

            bool askedAny = false;
            foreach (var (info, _) in targets)
            {
                try
                {
                    using var proc = Process.GetProcessById(info.Pid); // safe: our open handle pins this PID
                    if (proc.MainWindowHandle != 0 && proc.CloseMainWindow()) askedAny = true;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                    // Already gone or no window; it is handled below.
                }
            }

            if (askedAny && t.GracefulStopSeconds > 0)
            {
                var deadline = DateTime.UtcNow.AddSeconds(t.GracefulStopSeconds);
                while (DateTime.UtcNow < deadline && targets.Any(x => IsAlive(x.Handle)))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            int closed = 0, killed = 0;
            foreach (var (info, h) in targets)
            {
                if (!IsAlive(h))
                {
                    closed++;
                    continue;
                }

                if (Win32.TerminateProcess(h, 1))
                {
                    killed++;
                }
                else
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (IsAlive(h))
                    {
                        errors.Add($"Couldn't end {info.Name} ({info.Pid}): {new Win32Exception(err).Message}. It may be running as administrator.");
                    }
                }
            }

            foreach (var (_, h) in targets)
            {
                Win32.WaitForSingleObject(h, 3000);
            }

            return new StopResult(closed, killed, errors, unloaded);
        }
        finally
        {
            foreach (var (_, h) in targets) Win32.CloseHandle(h);
        }
    }

    /// <summary>
    /// Opens the process only if it is still the same one the monitor saw (start times must match
    /// exactly; an unreadable start time counts as a mismatch). Returns 0 otherwise.
    /// </summary>
    private static nint OpenVerified(ProcInfo p)
    {
        if (p.CreateTime == 0) return 0;
        nint h = Win32.OpenProcess(Win32.ProcessTerminate | Win32.ProcessQueryLimitedInformation | Win32.Synchronize, false, (uint)p.Pid);
        if (h == 0) return 0;
        if (Win32.GetProcessTimes(h, out long created, out _, out _, out _) && created == p.CreateTime)
        {
            return h;
        }

        Win32.CloseHandle(h);
        return 0;
    }

    private static bool IsAlive(nint handle) => Win32.WaitForSingleObject(handle, 0) == Win32.WaitTimeout;

    private static HashSet<int> SelfAndAncestors()
    {
        var set = new HashSet<int> { Environment.ProcessId };
        var all = new ProcessScanner().Scan();
        int pid = Environment.ProcessId;
        for (int i = 0; i < 32 && all.TryGetValue(pid, out var p) && p.ParentPid > 4 && set.Add(p.ParentPid); i++)
        {
            pid = p.ParentPid;
        }

        return set;
    }


    private static void RotateLog(string log)
    {
        try
        {
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                File.Move(log, log + ".old", overwrite: true);
            }
        }
        catch (IOException) { /* log in use; keep appending */ }
    }
}
