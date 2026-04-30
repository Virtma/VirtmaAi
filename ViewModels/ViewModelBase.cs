using CommunityToolkit.Mvvm.ComponentModel;

namespace VirtmaAi.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    private static readonly TimeSpan ErrorAutoClearDelay = TimeSpan.FromSeconds(6);

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    private CancellationTokenSource? _errorClearCts;

    partial void OnErrorMessageChanged(string? value)
    {
        // Cancel any pending clear regardless — we either set a new value (which schedules a fresh clear)
        // or the value was cleared explicitly (no need for a future clear).
        _errorClearCts?.Cancel();
        _errorClearCts?.Dispose();
        _errorClearCts = null;

        if (string.IsNullOrEmpty(value)) return;

        var cts = new CancellationTokenSource();
        _errorClearCts = cts;
        var token = cts.Token;

        // Self-dismiss after a delay so error labels behave like web toasts.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ErrorAutoClearDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                if (ReferenceEquals(_errorClearCts, cts) && string.Equals(ErrorMessage, value, StringComparison.Ordinal))
                    ErrorMessage = null;
            }
            else
            {
                dispatcher.Dispatch(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (ReferenceEquals(_errorClearCts, cts) && string.Equals(ErrorMessage, value, StringComparison.Ordinal))
                        ErrorMessage = null;
                });
            }
        }, token);
    }
}
