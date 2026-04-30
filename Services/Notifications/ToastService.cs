using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Notifications;

/// <summary>
/// In-process toast service. Pushes notifications into <see cref="Active"/> which the
/// <c>ToastHostView</c> overlay binds to. Auto-dismisses after a kind-dependent delay; logs every
/// notification to the error log for the Logs page.
/// </summary>
public sealed class ToastService : IToastService
{
    private readonly IErrorLogService _errorLog;
    private readonly ILogger<ToastService> _logger;

    public ObservableCollection<ToastNotification> Active { get; } = new();

    public ToastService(IErrorLogService errorLog, ILogger<ToastService> logger)
    {
        _errorLog = errorLog;
        _logger = logger;
    }

    public Task ShowAsync(string message, ToastKind kind = ToastKind.Info)
    {
        if (string.IsNullOrWhiteSpace(message)) return Task.CompletedTask;

        // Mirror to the persistent error log so users can review what flashed by.
        switch (kind)
        {
            case ToastKind.Error:   _errorLog.AppendError(message); break;
            case ToastKind.Warning: _errorLog.AppendWarning(message); break;
            default:                _errorLog.AppendInfo(message); break;
        }

        var notif = new ToastNotification(this) { Message = message, Kind = kind };
        var duration = kind switch
        {
            ToastKind.Error   => TimeSpan.FromSeconds(7),
            ToastKind.Warning => TimeSpan.FromSeconds(5),
            _                 => TimeSpan.FromSeconds(3),
        };

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // Headless / no UI — just log.
            _logger.LogInformation("[toast/{Kind}] {Message}", kind, message);
            return Task.CompletedTask;
        }

        dispatcher.Dispatch(() =>
        {
            try
            {
                Active.Add(notif);
                // Cap the on-screen stack so a flood of errors can't fill the page.
                while (Active.Count > 5) Active.RemoveAt(0);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Toast add failed; message was {Message}", message);
            }
        });

        // Auto-dismiss — schedule on the dispatcher so the removal happens on the UI thread.
        dispatcher.DispatchDelayed(duration, () =>
        {
            try { Active.Remove(notif); }
            catch (Exception ex) { _logger.LogDebug(ex, "Toast remove failed"); }
        });

        return Task.CompletedTask;
    }

    public Task SuccessAsync(string message) => ShowAsync(message, ToastKind.Success);
    public Task ErrorAsync(string message)   => ShowAsync(message, ToastKind.Error);
    public Task WarningAsync(string message) => ShowAsync(message, ToastKind.Warning);

    public void Dismiss(Guid id)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Remove()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (Active[i].Id == id) { Active.RemoveAt(i); break; }
            }
        }
        if (dispatcher is null) Remove();
        else dispatcher.Dispatch(Remove);
    }
}
