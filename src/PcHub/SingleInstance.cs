using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using PcHub.Services;

namespace PcHub;

/// <summary>
/// One PCHUB per user. A second launch hands its message to the running copy over a named pipe that only the
/// same user account can connect to (works on Windows and macOS), then exits.
/// Messages: "SHOW" (bring the window forward) or "OPEN &lt;url&gt;" (a tool asked to open a web page).
/// </summary>
internal static class SingleInstance
{
    private const int MaxMessage = 4096;
    private static Mutex? _mutex;
    private static readonly string Id = "PCHUB-" + Sanitize(Environment.UserName);

    // The listener starts as soon as this copy knows it is the first, before the window exists, so a launch that
    // arrives during a slow cold start is queued instead of timing out. Messages wait here until Attach.
    private static readonly ConcurrentQueue<string> Early = new();
    private static readonly CancellationTokenSource ListenCts = new();
    private static Action<string>? _handler;
    private static readonly object Gate = new();

    /// <summary>
    /// True when this process should run the app. False when it handed off to a running copy (or restarted
    /// itself detached for a tool's browser request) and must exit now.
    /// </summary>
    public static bool TryBecomeFirst(string[] args, out string? startupUrl)
    {
        bool fromTool = args.Length >= 1 && IsWebAddress(args[0]);
        startupUrl = fromTool ? args[0]
            : args.Length >= 2 && args[0] == "--open" && IsWebAddress(args[1]) ? args[1] : null;
        string message = startupUrl is null ? "SHOW" : "OPEN " + startupUrl;

        bool first;
        try
        {
            _mutex = new Mutex(true, Id, out first);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            first = true; // can't tell: fall back to the pipe check below
        }

        // On macOS a named mutex may not be shared across login sessions (Python starts BROWSER in a new
        // session), so also ask the pipe: if a running PCHUB answers, it is the first.
        if (first && !OperatingSystem.IsWindows() && TrySend(message, 300)) first = false;

        if (!first)
        {
            if (!TrySend(message, 10000))
            {
                try { CrashLog.Write("Couldn't reach the running PCHUB", null); }
                catch (Exception) { /* this copy is exiting anyway */ }
            }

            return false;
        }

        if (fromTool)
        {
            // A tool asked to "open its browser" while PCHUB wasn't running. Python waits for that command to
            // finish, so start PCHUB separately and return at once instead of blocking the tool.
            Release();
            if (Environment.ProcessPath is { } self)
            {
                var psi = new ProcessStartInfo(self) { UseShellExecute = false };
                psi.ArgumentList.Add("--open");
                psi.ArgumentList.Add(startupUrl!);
                Process.Start(psi)?.Dispose();
            }

            return false;
        }

        StartListener(ListenCts.Token);
        return true;
    }

    /// <summary>Delivers messages from later launches (queued ones first); calls back on a background thread.</summary>
    public static void Attach(Action<string> onMessage)
    {
        lock (Gate)
        {
            _handler = onMessage;
            while (Early.TryDequeue(out string? m)) onMessage(m);
        }
    }

    public static void Stop() => ListenCts.Cancel();

    private static void Deliver(string msg)
    {
        lock (Gate)
        {
            if (_handler is not null) _handler(msg);
            else if (Early.Count < 10) Early.Enqueue(msg);
        }
    }

    private static void StartListener(CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();

                    // A client that connects and then never finishes must not block later launches.
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(2));
                    var buf = new byte[MaxMessage];
                    int total = 0, n;
                    try
                    {
                        while (total < buf.Length && (n = server.ReadAsync(buf.AsMemory(total), readCts.Token).AsTask().GetAwaiter().GetResult()) > 0)
                        {
                            total += n;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        continue; // timed out reading: drop this one, keep listening
                    }

                    string msg = Encoding.UTF8.GetString(buf, 0, total).Trim();
                    if (msg.Length > 0) Deliver(msg);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Never let the listener die silently; a later launch would then do nothing at all.
                    try { CrashLog.Write("Single-instance listener", ex); }
                    catch (Exception) { /* logging must not stop the loop */ }
                    Thread.Sleep(1000);
                }
            }
        })
        { IsBackground = true, Name = "PCHUB hand-off" };
        thread.Start();
    }

    private static bool TrySend(string message, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(message.Length > MaxMessage ? message[..MaxMessage] : message);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Release()
    {
        if (_mutex is null) return;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned */ }
        _mutex.Dispose();
        _mutex = null;
    }

    public static bool IsWebAddress(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string Sanitize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Take(40).ToArray()) is { Length: > 0 } t ? t : "user";
}
