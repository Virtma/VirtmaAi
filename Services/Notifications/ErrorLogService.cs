using System.Collections.ObjectModel;

namespace VirtmaAi.Services.Notifications;

public sealed class ErrorLogService : IErrorLogService
{
    private const int MaxEntries = 500;

    private readonly ObservableCollection<LogEntry> _entries = new();
    private readonly object _lock = new();

    public ErrorLogService()
    {
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
    }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    public void AppendInfo(string message, string? detail = null) => Add(LogEntryKind.Info, message, detail);
    public void AppendWarning(string message, string? detail = null) => Add(LogEntryKind.Warning, message, detail);
    public void AppendError(string message, string? detail = null) => Add(LogEntryKind.Error, message, detail);

    public void Clear()
    {
        RunOnMainThread(() =>
        {
            lock (_lock) _entries.Clear();
        });
    }

    private void Add(LogEntryKind kind, string message, string? detail)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new LogEntry(DateTime.UtcNow, kind, message, detail);
        RunOnMainThread(() =>
        {
            lock (_lock)
            {
                _entries.Insert(0, entry);
                while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            }
        });
    }

    private static void RunOnMainThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.IsDispatchRequired == false) action();
        else dispatcher.Dispatch(action);
    }
}
