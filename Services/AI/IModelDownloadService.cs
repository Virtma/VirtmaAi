namespace VirtmaAi.Services.AI;

/// <summary>
/// Streaming HTTP downloader for local model files (GGUF, safetensors, etc.) with rich progress
/// reporting (bytes downloaded, total, throughput, ETA) and resume support.
/// </summary>
public interface IModelDownloadService
{
    /// <summary>
    /// Default destination directory for downloaded models. Auto-created on first use.
    /// </summary>
    string DefaultModelsDirectory { get; }

    /// <summary>
    /// Streams a model file to disk, reporting progress on the supplied <paramref name="progress"/>
    /// channel. The returned path is the final on-disk file. Throws on cancellation or transport
    /// failure.
    /// </summary>
    Task<string> DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct);
}

public sealed record ModelDownloadProgress(
    long BytesDownloaded,
    long? TotalBytes,
    double? PercentComplete,
    double BytesPerSecond,
    TimeSpan? Eta,
    string Stage,
    string? DestinationPath);
