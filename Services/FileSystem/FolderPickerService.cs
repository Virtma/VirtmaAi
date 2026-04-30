using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.FileSystem;

public sealed class FolderPickerService : IFolderPickerService
{
    private readonly ILogger<FolderPickerService> _logger;

    public FolderPickerService(ILogger<FolderPickerService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> PickFolderAsync(string? initialPath = null, CancellationToken ct = default)
    {
#if WINDOWS
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
            };
            picker.FileTypeFilter.Add("*");

            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            var handler = window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (handler is not null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(handler);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            var folder = await picker.PickSingleFolderAsync().AsTask(ct).ConfigureAwait(false);
            return folder?.Path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Folder picker failed");
            return null;
        }
#else
        await Task.CompletedTask;
        _logger.LogInformation("Native folder picker unavailable on this platform; caller should fall back to a text field.");
        return initialPath;
#endif
    }
}
