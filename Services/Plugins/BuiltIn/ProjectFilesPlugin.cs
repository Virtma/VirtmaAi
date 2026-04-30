using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.FileSystem;

namespace VirtmaAi.Services.Plugins.BuiltIn;

public sealed class ProjectFilesPlugin : IBuiltInPlugin
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ISandboxedFileSystem _sandbox;
    private readonly ILogger<ProjectFilesPlugin> _logger;

    public ProjectFilesPlugin(ISandboxedFileSystem sandbox, ILogger<ProjectFilesPlugin> logger)
    {
        _sandbox = sandbox;
        _logger = logger;
    }

    public string Name => "project-files";
    public string Description => "Read, write, list, and delete files inside the active conversation's project directory (the sandbox).";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        if (_sandbox.ProjectRoot is null)
            return new PluginInvocationResult(false, string.Empty,
                "No project directory is set on this conversation. Ask the user to pick one (Browse… in the project bar) and try again.");

        FileCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<FileCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        try
        {
            return cmd.Action?.ToLowerInvariant() switch
            {
                "read-file" => await ReadFileAsync(cmd, ct),
                "write-file" => await WriteFileAsync(cmd, ct),
                "list-files" => ListFiles(cmd),
                "delete-file" => DeleteFile(cmd),
                "project-root" => new PluginInvocationResult(true, _sandbox.ProjectRoot ?? string.Empty),
                "insert-at-line"   => await InsertAtLineAsync(cmd, ct),
                "insert-at-offset" => await InsertAtOffsetAsync(cmd, ct),
                "replace-lines"    => await ReplaceLinesAsync(cmd, ct),
                "replace-text"     => await ReplaceTextAsync(cmd, ct),
                _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
            };
        }
        catch (SandboxViolationException ex)
        {
            return new PluginInvocationResult(false, string.Empty, "sandbox violation: " + ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "project-files {Action} failed", cmd.Action);
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private async Task<PluginInvocationResult> ReadFileAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "read-file requires 'path'");
        var text = await _sandbox.ReadTextAsync(cmd.Path, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, text);
    }

    private async Task<PluginInvocationResult> WriteFileAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "write-file requires 'path'");
        await _sandbox.WriteTextAsync(cmd.Path, cmd.Content ?? string.Empty, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"wrote {cmd.Path}");
    }

    private PluginInvocationResult ListFiles(FileCommand cmd)
    {
        var path = string.IsNullOrWhiteSpace(cmd.Path) ? "." : cmd.Path;
        var pattern = string.IsNullOrWhiteSpace(cmd.Pattern) ? "*" : cmd.Pattern;
        var sb = new StringBuilder();
        foreach (var f in _sandbox.EnumerateFiles(path, pattern, cmd.Recursive ?? false).OrderBy(s => s))
        {
            var rel = Path.GetRelativePath(_sandbox.ProjectRoot!, f);
            sb.AppendLine(rel);
        }
        return new PluginInvocationResult(true, sb.Length == 0 ? "(empty)" : sb.ToString());
    }

    private PluginInvocationResult DeleteFile(FileCommand cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "delete-file requires 'path'");
        var combined = Path.IsPathRooted(cmd.Path)
            ? Path.GetFullPath(cmd.Path)
            : Path.GetFullPath(Path.Combine(_sandbox.ProjectRoot!, cmd.Path));
        if (!_sandbox.IsInsideSandbox(combined))
            return new PluginInvocationResult(false, string.Empty, "path escapes sandbox: " + combined);
        if (!File.Exists(combined))
            return new PluginInvocationResult(false, string.Empty, "file not found: " + combined);
        File.Delete(combined);
        return new PluginInvocationResult(true, "deleted " + cmd.Path);
    }

    /// <summary>Insert <c>cmd.Content</c> before line <c>cmd.Line</c> (1-based). Use Line=1 to prepend, Line=N+1 to append.</summary>
    private async Task<PluginInvocationResult> InsertAtLineAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "insert-at-line requires 'path'");
        if (cmd.Line is null || cmd.Line < 1)
            return new PluginInvocationResult(false, string.Empty, "insert-at-line requires 'line' >= 1");

        var existing = await _sandbox.ReadTextAsync(cmd.Path, ct).ConfigureAwait(false);
        var (lines, sep) = SplitLines(existing);
        var idx = Math.Min(cmd.Line.Value - 1, lines.Count); // clamp to end
        var insertion = (cmd.Content ?? string.Empty);
        // Normalize the inserted block to use the file's existing line separator.
        var normalized = insertion.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", sep);
        if (!normalized.EndsWith(sep, StringComparison.Ordinal)) normalized += sep;
        lines.Insert(idx, normalized);
        var result = string.Concat(lines);
        await _sandbox.WriteTextAsync(cmd.Path, result, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"inserted {insertion.Length} char(s) at line {cmd.Line} of {cmd.Path}");
    }

    /// <summary>Insert <c>cmd.Content</c> at byte/char offset <c>cmd.Offset</c> (0-based) without rewriting the rest of the file.</summary>
    private async Task<PluginInvocationResult> InsertAtOffsetAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "insert-at-offset requires 'path'");
        if (cmd.Offset is null || cmd.Offset < 0)
            return new PluginInvocationResult(false, string.Empty, "insert-at-offset requires 'offset' >= 0");

        var existing = await _sandbox.ReadTextAsync(cmd.Path, ct).ConfigureAwait(false);
        var off = Math.Min(cmd.Offset.Value, existing.Length);
        var inserted = cmd.Content ?? string.Empty;
        var result = existing.Substring(0, off) + inserted + existing.Substring(off);
        await _sandbox.WriteTextAsync(cmd.Path, result, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"inserted {inserted.Length} char(s) at offset {off} of {cmd.Path}");
    }

    /// <summary>Replace lines [<c>StartLine</c>, <c>EndLine</c>] inclusive (1-based) with <c>cmd.Content</c>. Both bounds clamp to the file length.</summary>
    private async Task<PluginInvocationResult> ReplaceLinesAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "replace-lines requires 'path'");
        if (cmd.StartLine is null || cmd.EndLine is null || cmd.StartLine < 1 || cmd.EndLine < cmd.StartLine)
            return new PluginInvocationResult(false, string.Empty, "replace-lines requires startLine >= 1 and endLine >= startLine");

        var existing = await _sandbox.ReadTextAsync(cmd.Path, ct).ConfigureAwait(false);
        var (lines, sep) = SplitLines(existing);
        var start = Math.Min(cmd.StartLine.Value - 1, lines.Count);
        var endExclusive = Math.Min(cmd.EndLine.Value, lines.Count);
        if (endExclusive > start) lines.RemoveRange(start, endExclusive - start);

        var replacement = cmd.Content ?? string.Empty;
        var normalized = replacement.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", sep);
        if (normalized.Length > 0 && !normalized.EndsWith(sep, StringComparison.Ordinal)) normalized += sep;
        if (normalized.Length > 0) lines.Insert(start, normalized);

        var result = string.Concat(lines);
        await _sandbox.WriteTextAsync(cmd.Path, result, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"replaced lines {cmd.StartLine}..{cmd.EndLine} of {cmd.Path}");
    }

    /// <summary>
    /// Find the first occurrence of <c>cmd.Find</c> and replace with <c>cmd.Replace</c>. Errors out if
    /// not found OR if it appears more than once and <c>cmd.AllowMultiple</c> isn't true. This is
    /// the safest "edit a code file" primitive — refuses to make ambiguous edits.
    /// </summary>
    private async Task<PluginInvocationResult> ReplaceTextAsync(FileCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Path))
            return new PluginInvocationResult(false, string.Empty, "replace-text requires 'path'");
        if (string.IsNullOrEmpty(cmd.Find))
            return new PluginInvocationResult(false, string.Empty, "replace-text requires 'find' (non-empty)");

        var existing = await _sandbox.ReadTextAsync(cmd.Path, ct).ConfigureAwait(false);
        int firstIdx = existing.IndexOf(cmd.Find, StringComparison.Ordinal);
        if (firstIdx < 0)
            return new PluginInvocationResult(false, string.Empty, "find target not present in file — copy more surrounding context");

        // Count occurrences (cheap because we already have the index of the first).
        int next = existing.IndexOf(cmd.Find, firstIdx + cmd.Find.Length, StringComparison.Ordinal);
        if (next >= 0 && cmd.AllowMultiple != true)
            return new PluginInvocationResult(false, string.Empty,
                "find target appears more than once — pass allowMultiple:true or include more surrounding context");

        var replace = cmd.Replace ?? string.Empty;
        var result = cmd.AllowMultiple == true
            ? existing.Replace(cmd.Find, replace)
            : existing.Substring(0, firstIdx) + replace + existing.Substring(firstIdx + cmd.Find.Length);
        await _sandbox.WriteTextAsync(cmd.Path, result, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"replaced {cmd.Find.Length} char(s) → {replace.Length} char(s) in {cmd.Path}");
    }

    /// <summary>
    /// Split text into lines while preserving the existing line separator. Each entry in the
    /// returned list is a line *with* its trailing separator, so concatenating them recreates the
    /// original. The detected separator is also returned for inserts.
    /// </summary>
    private static (List<string> Lines, string Separator) SplitLines(string text)
    {
        // Detect dominant line separator. Default to platform's NewLine if file is empty / single-line.
        string sep = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
                   : text.Contains('\n', StringComparison.Ordinal) ? "\n"
                   : Environment.NewLine;

        var lines = new List<string>();
        if (text.Length == 0) return (lines, sep);

        int idx = 0;
        while (idx < text.Length)
        {
            int found = text.IndexOf(sep, idx, StringComparison.Ordinal);
            if (found < 0) { lines.Add(text.Substring(idx)); break; }
            lines.Add(text.Substring(idx, found + sep.Length - idx));
            idx = found + sep.Length;
        }
        return (lines, sep);
    }

    private sealed class FileCommand
    {
        public string? Action { get; set; }
        public string? Path { get; set; }
        public string? Content { get; set; }
        public string? Pattern { get; set; }
        public bool? Recursive { get; set; }

        // ===== positional / surgical edits =====
        /// <summary>1-based line number for insert-at-line.</summary>
        public int? Line { get; set; }
        /// <summary>0-based character offset for insert-at-offset.</summary>
        public int? Offset { get; set; }
        /// <summary>1-based inclusive start line for replace-lines.</summary>
        public int? StartLine { get; set; }
        /// <summary>1-based inclusive end line for replace-lines.</summary>
        public int? EndLine { get; set; }
        /// <summary>Exact substring to find for replace-text. Must be unique unless AllowMultiple is true.</summary>
        public string? Find { get; set; }
        /// <summary>Replacement substring for replace-text.</summary>
        public string? Replace { get; set; }
        /// <summary>Allow replace-text to act on multiple occurrences.</summary>
        public bool? AllowMultiple { get; set; }
    }
}
