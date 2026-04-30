using System.Collections.ObjectModel;

namespace VirtmaAi.Services.Notifications;

public enum LogEntryKind
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record LogEntry(DateTime TimestampUtc, LogEntryKind Kind, string Message, string? Detail);

public interface IErrorLogService
{
    ReadOnlyObservableCollection<LogEntry> Entries { get; }
    void AppendInfo(string message, string? detail = null);
    void AppendWarning(string message, string? detail = null);
    void AppendError(string message, string? detail = null);
    void Clear();
}
