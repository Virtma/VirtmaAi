using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VirtmaAi.Services.Notifications;

/// <summary>
/// A single live toast item rendered by <c>ToastHostView</c>. The service owns the lifecycle —
/// items are added on Show and removed when their auto-dismiss timer fires (or the user clicks ×).
/// </summary>
public sealed partial class ToastNotification : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private ToastKind _kind;

    public string Icon => Kind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Warning => "⚠",
        ToastKind.Error   => "✕",
        _                 => "ℹ",
    };

    public Color BackgroundColor => Kind switch
    {
        ToastKind.Success => Color.FromArgb("#16A34A"), // green-600
        ToastKind.Warning => Color.FromArgb("#D97706"), // amber-600
        ToastKind.Error   => Color.FromArgb("#DC2626"), // red-600
        _                 => Color.FromArgb("#2563EB"), // blue-600
    };

    public IRelayCommand DismissCommand { get; }
    public IRelayCommand CopyCommand { get; }

    public ToastNotification(IToastService service)
    {
        DismissCommand = new RelayCommand(() => service.Dismiss(Id));
        CopyCommand = new RelayCommand(async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(Message))
                    await Clipboard.Default.SetTextAsync(Message);
            }
            catch { /* best-effort — clipboard may be locked */ }
        });
    }
}
