using Serilog.Core;
using Serilog.Events;

namespace VirtmaAi.Services.Notifications;

public sealed class ErrorLogSink : ILogEventSink
{
    private readonly IErrorLogService _log;

    public ErrorLogSink(IErrorLogService log)
    {
        _log = log;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Warning) return;

        var message = logEvent.RenderMessage();
        var detail = logEvent.Exception?.ToString();

        if (logEvent.Level >= LogEventLevel.Error)
            _log.AppendError(message, detail);
        else
            _log.AppendWarning(message, detail);
    }
}
