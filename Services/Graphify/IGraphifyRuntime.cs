namespace VirtmaAi.Services.Graphify;

public interface IGraphifyRuntime
{
    bool IsDesktop { get; }
    GraphifyRuntimeStatus Probe();
    Task<GraphifyRuntimeStatus> EnsureInstalledAsync(IProgress<GraphifyInstallProgress>? progress, CancellationToken ct);

    string PythonExecutable { get; }
    string UvExecutable { get; }
    string RuntimeRoot { get; }
}

public sealed record GraphifyRuntimeStatus(
    bool RuntimeReady,
    bool GraphifyInstalled,
    string? PythonVersion,
    string? GraphifyVersion,
    string? Error);

public sealed record GraphifyInstallProgress(string Stage, double? Percent = null, string? Detail = null);
