namespace VirtmaAi.Services.FileSystem;

public interface IFilePickerService
{
    Task<string?> PickFileAsync(string? title = null, IEnumerable<string>? fileTypes = null, CancellationToken ct = default);
}
