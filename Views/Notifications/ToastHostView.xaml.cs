using System.Collections.ObjectModel;
using VirtmaAi.Services.Notifications;

namespace VirtmaAi.Views.Notifications;

public partial class ToastHostView : ContentView
{
    public static readonly BindableProperty ToastsProperty = BindableProperty.Create(
        nameof(Toasts),
        typeof(ObservableCollection<ToastNotification>),
        typeof(ToastHostView),
        defaultValue: null);

    public ObservableCollection<ToastNotification>? Toasts
    {
        get => (ObservableCollection<ToastNotification>?)GetValue(ToastsProperty);
        set => SetValue(ToastsProperty, value);
    }

    public ToastHostView()
    {
        InitializeComponent();
        // Resolve the singleton service if no Toasts collection was bound explicitly.
        Loaded += (_, _) =>
        {
            if (Toasts is not null) return;
            try
            {
                var sp = (Application.Current as IPlatformApplication)?.Services
                         ?? IPlatformApplication.Current?.Services;
                if (sp?.GetService(typeof(IToastService)) is IToastService svc)
                    Toasts = svc.Active;
            }
            catch { /* host is best-effort — don't crash a page render */ }
        };
    }
}
