namespace VirtmaAi.Services.FileSystem;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string? initialPath = null, CancellationToken ct = default);
}
