using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Allows the AI to read/modify VirtmaAi's own source tree — but ONLY after the user has
/// explicitly enabled the "AI may self-modify" toggle in Settings. Without that flag, every call
/// fails with a precise error so the AI can tell the user how to enable it.
///
/// The plugin is intentionally separate from <c>project-files</c> (which is sandboxed to the
/// active conversation's project dir) so self-modification can never happen by accident.
/// </summary>
public sealed class AppSelfModifyPlugin : IBuiltInPlugin
{
    public const string AllowSettingKey = "ai.selfmodify.allowed";
    public const string AppRootSettingKey = "ai.selfmodify.appRoot";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ISettingsService _settings;
    private readonly ILogger<AppSelfModifyPlugin> _logger;

    public AppSelfModifyPlugin(ISettingsService settings, ILogger<AppSelfModifyPlugin> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string Name => "app-selfmodify";
    public string Description =>
        "Read or modify VirtmaAi's own source tree. **Disabled by default** — the user must " +
        "enable it in Settings → AI Permissions before any call succeeds. Supports the same " +
        "actions as project-files (read-file, write-file, list-files, insert-at-line, " +
        "insert-at-offset, replace-lines, replace-text, delete-file).";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        if (!_settings.Get<bool>(AllowSettingKey))
        {
            return new PluginInvocationResult(false, string.Empty,
                "App self-modification is disabled. Tell the user to open Settings → AI Permissions " +
                "and enable 'Allow AI to modify VirtmaAi'. Until then, this plugin will refuse every call.");
        }

        var appRoot = _settings.Get<string>(AppRootSettingKey);
        if (string.IsNullOrWhiteSpace(appRoot) || !Directory.Exists(appRoot))
        {
            return new PluginInvocationResult(false, string.Empty,
                "App source root is not configured. The user should open Settings → AI Permissions " +
                "and pick the VirtmaAi source directory.");
        }

        SelfModCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<SelfModCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        try
        {
            return cmd.Action?.ToLowerInvariant() switch
            {
                "read-file"        => await ReadFileAsync(appRoot, cmd, ct),
                "write-file"       => await WriteFileAsync(appRoot, cmd, ct),
                "list-files"       => ListFiles(appRoot, cmd),
                "delete-file"      => DeleteFile(appRoot, cmd),
                "insert-at-line"   => await InsertAtLineAsync(appRoot, cmd, ct),
                "insert-at-offset" => await InsertAtOffsetAsync(appRoot, cmd, ct),
                "replace-lines"    => await ReplaceLinesAsync(appRoot, cmd, ct),
                "replace-text"     => await ReplaceTextAsync(appRoot, cmd, ct),
                "app-root"         => new PluginInvocationResult(true, appRoot),
                _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "app-selfmodify {Action} failed", cmd.Action);
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    // ===== Path resolution + safety =====

    private static string ResolveSafe(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("'path' is required");
        var combined = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("path escapes app root: " + combined);
        return combined;
    }

    private async Task<PluginInvocationResult> ReadFileAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        if (!File.Exists(p)) return new PluginInvocationResult(false, string.Empty, "file not found: " + p);
        var text = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, text);
    }

    private async Task<PluginInvocationResult> WriteFileAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, cmd.Content ?? string.Empty, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"wrote {p}");
    }

    private PluginInvocationResult ListFiles(string root, SelfModCommand cmd)
    {
        var dir = ResolveSafe(root, cmd.Path ?? ".");
        if (!Directory.Exists(dir)) return new PluginInvocationResult(false, string.Empty, "not a directory: " + dir);
        var pattern = string.IsNullOrWhiteSpace(cmd.Pattern) ? "*" : cmd.Pattern;
        var sb = new StringBuilder();
        foreach (var f in Directory.EnumerateFiles(dir, pattern, cmd.Recursive == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).OrderBy(s => s))
            sb.AppendLine(Path.GetRelativePath(root, f));
        return new PluginInvocationResult(true, sb.Length == 0 ? "(empty)" : sb.ToString());
    }

    private PluginInvocationResult DeleteFile(string root, SelfModCommand cmd)
    {
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        if (!File.Exists(p)) return new PluginInvocationResult(false, string.Empty, "file not found: " + p);
        File.Delete(p);
        return new PluginInvocationResult(true, "deleted " + p);
    }

    private async Task<PluginInvocationResult> InsertAtLineAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        if (cmd.Line is null || cmd.Line < 1) return new PluginInvocationResult(false, string.Empty, "insert-at-line requires 'line' >= 1");
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        var existing = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
        var (lines, sep) = SplitLines(existing);
        var idx = Math.Min(cmd.Line.Value - 1, lines.Count);
        var insertion = (cmd.Content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", sep);
        if (!insertion.EndsWith(sep, StringComparison.Ordinal)) insertion += sep;
        lines.Insert(idx, insertion);
        await File.WriteAllTextAsync(p, string.Concat(lines), ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"inserted at line {cmd.Line} of {p}");
    }

    private async Task<PluginInvocationResult> InsertAtOffsetAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        if (cmd.Offset is null || cmd.Offset < 0) return new PluginInvocationResult(false, string.Empty, "insert-at-offset requires 'offset' >= 0");
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        var existing = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
        var off = Math.Min(cmd.Offset.Value, existing.Length);
        await File.WriteAllTextAsync(p, existing.Substring(0, off) + (cmd.Content ?? "") + existing.Substring(off), ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"inserted at offset {off} of {p}");
    }

    private async Task<PluginInvocationResult> ReplaceLinesAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        if (cmd.StartLine is null || cmd.EndLine is null || cmd.StartLine < 1 || cmd.EndLine < cmd.StartLine)
            return new PluginInvocationResult(false, string.Empty, "replace-lines requires startLine >= 1 and endLine >= startLine");
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        var existing = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
        var (lines, sep) = SplitLines(existing);
        var start = Math.Min(cmd.StartLine.Value - 1, lines.Count);
        var endExclusive = Math.Min(cmd.EndLine.Value, lines.Count);
        if (endExclusive > start) lines.RemoveRange(start, endExclusive - start);
        var replacement = (cmd.Content ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", sep);
        if (replacement.Length > 0 && !replacement.EndsWith(sep, StringComparison.Ordinal)) replacement += sep;
        if (replacement.Length > 0) lines.Insert(start, replacement);
        await File.WriteAllTextAsync(p, string.Concat(lines), ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"replaced lines {cmd.StartLine}..{cmd.EndLine} of {p}");
    }

    private async Task<PluginInvocationResult> ReplaceTextAsync(string root, SelfModCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cmd.Find)) return new PluginInvocationResult(false, string.Empty, "replace-text requires non-empty 'find'");
        var p = ResolveSafe(root, cmd.Path ?? string.Empty);
        var existing = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
        int firstIdx = existing.IndexOf(cmd.Find, StringComparison.Ordinal);
        if (firstIdx < 0) return new PluginInvocationResult(false, string.Empty, "find target not present");
        int next = existing.IndexOf(cmd.Find, firstIdx + cmd.Find.Length, StringComparison.Ordinal);
        if (next >= 0 && cmd.AllowMultiple != true)
            return new PluginInvocationResult(false, string.Empty, "find target appears more than once — include more context or pass allowMultiple:true");
        var replace = cmd.Replace ?? string.Empty;
        var result = cmd.AllowMultiple == true
            ? existing.Replace(cmd.Find, replace)
            : existing.Substring(0, firstIdx) + replace + existing.Substring(firstIdx + cmd.Find.Length);
        await File.WriteAllTextAsync(p, result, ct).ConfigureAwait(false);
        return new PluginInvocationResult(true, $"replaced text in {p}");
    }

    private static (List<string> Lines, string Separator) SplitLines(string text)
    {
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

    private sealed class SelfModCommand
    {
        public string? Action { get; set; }
        public string? Path { get; set; }
        public string? Content { get; set; }
        public string? Pattern { get; set; }
        public bool? Recursive { get; set; }
        public int? Line { get; set; }
        public int? Offset { get; set; }
        public int? StartLine { get; set; }
        public int? EndLine { get; set; }
        public string? Find { get; set; }
        public string? Replace { get; set; }
        public bool? AllowMultiple { get; set; }
    }
}
