using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.FileSystem;

public sealed class SandboxedFileSystem : ISandboxedFileSystem
{
    private readonly ILogger<SandboxedFileSystem> _logger;
    private string? _projectRoot;

    public SandboxedFileSystem(ILogger<SandboxedFileSystem> logger)
    {
        _logger = logger;
    }

    public string? ProjectRoot => _projectRoot;

    public void SetProjectRoot(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            _projectRoot = null;
            return;
        }
        var full = Path.GetFullPath(absolutePath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException(full);
        _projectRoot = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger.LogInformation("Sandbox root: {Root}", _projectRoot);
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        return await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(path);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(resolved, content, cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false)
    {
        var resolved = Resolve(path);
        return Directory.EnumerateFiles(
            resolved,
            searchPattern,
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
    }

    public bool IsInsideSandbox(string absolutePath)
    {
        if (_projectRoot is null) return false;
        var full = Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.StartsWith(_projectRoot, StringComparison.OrdinalIgnoreCase);
    }

    private string Resolve(string path)
    {
        if (_projectRoot is null)
            throw new InvalidOperationException("Project root not set — cannot resolve sandboxed path");
        var combined = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(_projectRoot, path));
        if (!IsInsideSandbox(combined))
            throw new SandboxViolationException(combined, $"Path escapes sandbox: {combined}");
        return combined;
    }
}
