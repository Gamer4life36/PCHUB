using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;

namespace PcHub.Services;

public sealed class ActivityEntry
{
    public required DateTime Time { get; init; }
    public required string Tool { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public bool IsError => Level == "Error";
    public bool IsWarn => Level == "Warn";
    public string TimeText => Time.Date == DateTime.Today ? Time.ToString("HH:mm:ss") : Time.ToString("MMM d HH:mm");
}

/// <summary>Activity history: newest first in the UI (capped), appended to activity.jsonl on a background writer.</summary>
public sealed class ActivityLog : IDisposable
{
    private const int MaxShown = 400;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private readonly Dispatcher _ui;
    private readonly Channel<ActivityRecord> _queue = Channel.CreateUnbounded<ActivityRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;

    public ActivityLog(Dispatcher ui)
    {
        _ui = ui;
        LoadRecent();
        _writer = Task.Run(WriteLoopAsync);
    }

    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    public void Info(string tool, string message) => Add(tool, "Info", message);
    public void Warn(string tool, string message) => Add(tool, "Warn", message);
    public void Error(string tool, string message) => Add(tool, "Error", message);

    public void Add(string tool, string level, string message)
    {
        var rec = new ActivityRecord { Time = DateTime.Now, Tool = tool, Level = level, Message = message };
        _queue.Writer.TryWrite(rec);
        if (_ui.CheckAccess()) Show(rec);
        else _ui.InvokeAsync(() => Show(rec));
    }

    private void Show(ActivityRecord r)
    {
        Entries.Insert(0, new ActivityEntry { Time = r.Time, Tool = r.Tool, Level = r.Level, Message = r.Message });
        while (Entries.Count > MaxShown) Entries.RemoveAt(Entries.Count - 1);
    }

    private void LoadRecent()
    {
        try
        {
            if (!File.Exists(AppPaths.ActivityFile)) return;
            var lines = File.ReadLines(AppPaths.ActivityFile).TakeLast(MaxShown);
            foreach (string line in lines)
            {
                ActivityRecord? r = null;
                try { r = JsonSerializer.Deserialize(line, LineJson.Default.ActivityRecord); }
                catch (JsonException) { /* skip a damaged line */ }
                if (r is not null)
                {
                    Entries.Insert(0, new ActivityEntry { Time = r.Time, Tool = r.Tool, Level = r.Level, Message = r.Message });
                }
            }
        }
        catch (IOException ex)
        {
            CrashLog.Write("Could not read activity history", ex);
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var rec in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var fi = new FileInfo(AppPaths.ActivityFile);
                if (fi.Exists && fi.Length > MaxFileBytes)
                {
                    File.Move(fi.FullName, fi.FullName + ".old", overwrite: true);
                }

                await File.AppendAllTextAsync(AppPaths.ActivityFile, JsonSerializer.Serialize(rec, LineJson.Default.ActivityRecord) + Environment.NewLine).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Keep the writer alive: one bad write must not stop all later history from saving.
                CrashLog.Write("Could not save activity entry", ex);
            }
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(1500); } catch (AggregateException) { /* best effort flush on exit */ }
    }
}
