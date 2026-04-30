namespace VirtmaAi.Services.FileSystem;

public interface ISandboxedFileSystem
{
    string? ProjectRoot { get; }
    void SetProjectRoot(string? absolutePath);

    Task<string> ReadTextAsync(string relativeOrAbsolutePath, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string relativeOrAbsolutePath, string content, CancellationToken cancellationToken = default);
    IEnumerable<string> EnumerateFiles(string relativeOrAbsolutePath, string searchPattern = "*", bool recursive = false);
    bool IsInsideSandbox(string absolutePath);
}

public sealed class SandboxViolationException : UnauthorizedAccessException
{
    public string AttemptedPath { get; }
    public SandboxViolationException(string attemptedPath, string message)
        : base(message) => AttemptedPath = attemptedPath;
}
