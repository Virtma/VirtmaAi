using System.Collections.ObjectModel;

namespace VirtmaAi.Services.Notifications;

public enum ToastKind
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

public interface IToastService
{
    /// <summary>Live toasts. Bound by <c>ToastHostView</c>; the service mutates this on the UI thread.</summary>
    ObservableCollection<ToastNotification> Active { get; }

    Task ShowAsync(string message, ToastKind kind = ToastKind.Info);
    Task SuccessAsync(string message);
    Task ErrorAsync(string message);
    Task WarningAsync(string message);

    void Dismiss(Guid id);
}
