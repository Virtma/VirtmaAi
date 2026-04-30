using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.FileSystem;

public sealed class FilePickerService : IFilePickerService
{
    private readonly ILogger<FilePickerService> _logger;

    public FilePickerService(ILogger<FilePickerService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> PickFileAsync(string? title = null, IEnumerable<string>? fileTypes = null, CancellationToken ct = default)
    {
        try
        {
            var options = new PickOptions
            {
                PickerTitle = title ?? "Pick a file"
            };
            var result = await FilePicker.Default.PickAsync(options).ConfigureAwait(false);
            return result?.FullPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File picker failed");
            return null;
        }
    }
}
