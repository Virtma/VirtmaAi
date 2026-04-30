using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Script Runner — allows the AI to write and execute scripts in Python, PowerShell,
/// Batch/CMD, Bash, JavaScript (Node.js), or Ruby.
///
/// <para>
/// The AI should use this plugin when no existing plugin covers what it needs to do.
/// It generates the script, invokes this plugin, and gets back stdout/stderr.
/// </para>
///
/// <para>
/// Input JSON shape:
/// <code>
/// {
///   "language": "python",           // python | powershell | batch | bash | node | ruby
///   "code":     "print('hello')",
///   "timeoutSeconds": 30            // optional, default 30, max 300
/// }
/// </code>
/// </para>
///
/// <para>
/// The script is written to a temp file, executed with the appropriate interpreter,
/// and the temp file is deleted on completion.  Stdout + stderr are returned; if the
/// process exits with a non-zero code it is included in the output.
/// </para>
/// </summary>
public sealed class ScriptRunnerPlugin : IBuiltInPlugin
{
    private readonly ILogger<ScriptRunnerPlugin> _logger;

    public ScriptRunnerPlugin(ILogger<ScriptRunnerPlugin> logger)
    {
        _logger = logger;
    }

    public string Name        => "script-runner";
    public string Description =>
        "Write and execute a script (Python, PowerShell, Batch, Bash, Node.js) directly on " +
        "this machine. Use this when no other plugin can accomplish the task — generate the " +
        "script, run it, and get back the output. " +
        "Input: { \"language\": \"python|powershell|batch|bash|node|ruby\", " +
        "\"code\": \"<script body>\", \"timeoutSeconds\": 30 }";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        ScriptCommand? cmd;
        try   { cmd = JsonSerializer.Deserialize<ScriptCommand>(input, JsonOpts); }
        catch (Exception ex) { return Fail("Invalid JSON: " + ex.Message); }
        if (cmd is null || string.IsNullOrWhiteSpace(cmd.Code))
            return Fail("'code' is required");
        if (string.IsNullOrWhiteSpace(cmd.Language))
            return Fail("'language' is required (python | powershell | batch | bash | node | ruby)");

        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(cmd.TimeoutSeconds.GetValueOrDefault(30), 1, 300));

        var lang = cmd.Language.Trim().ToLowerInvariant();
        var (exe, args, ext, supported) = ResolveInterpreter(lang);
        if (!supported)
            return Fail($"Unsupported language '{lang}'. Supported: python, powershell, batch, bash, node, ruby.");

        // Write the script to a temporary file.
        var tmpFile = Path.Combine(Path.GetTempPath(), $"virtmaai_script_{Guid.NewGuid():N}{ext}");
        try
        {
            await File.WriteAllTextAsync(tmpFile, cmd.Code, Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
            return Fail("Could not write temp script: " + ex.Message);
        }

        _logger.LogInformation("ScriptRunner: executing {Language} script '{File}'", lang, tmpFile);

        var fullArgs = string.Format(args, tmpFile);

        var psi = new ProcessStartInfo(exe, fullArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        int? exitCode = null;

        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await proc.WaitForExitAsync(linked.Token);
                exitCode = proc.ExitCode;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return Fail($"Script timed out after {timeout.TotalSeconds:0}s");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ScriptRunner: process start failed ({Language})", lang);
            return Fail($"Could not start interpreter '{exe}': {ex.Message}. " +
                        $"Make sure {lang} is installed and on the PATH.");
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }

        var stdout = Truncate(stdoutBuf.ToString().TrimEnd(), 16_000);
        var stderr = Truncate(stderrBuf.ToString().TrimEnd(), 4_000);

        var sb = new StringBuilder();
        if (stdout.Length > 0)
        {
            sb.AppendLine("### stdout");
            sb.AppendLine(stdout);
        }
        if (stderr.Length > 0)
        {
            sb.AppendLine("### stderr");
            sb.AppendLine(stderr);
        }
        if (exitCode is not null and not 0)
            sb.AppendLine($"### exit code: {exitCode}");
        if (sb.Length == 0)
            sb.AppendLine("(no output)");

        var success = exitCode is null or 0 && stderr.Length == 0;
        return new PluginInvocationResult(success, sb.ToString().TrimEnd(), exitCode is not 0 ? stderr : null, exitCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns (executable, argument-format-string-with-{0}-for-path, file-extension, isSupported).
    /// </summary>
    private static (string exe, string args, string ext, bool ok) ResolveInterpreter(string lang)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        return lang switch
        {
            "python" or "python3" or "py" =>
                (isWindows ? "python" : "python3", "\"{0}\"", ".py", true),

            "powershell" or "ps1" or "pwsh" =>
                (isWindows ? "powershell" : "pwsh",
                 isWindows ? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\""
                           : "-NoProfile -NonInteractive -File \"{0}\"",
                 ".ps1", true),

            "batch" or "bat" or "cmd" =>
                ("cmd.exe", "/c \"{0}\"", ".bat", isWindows), // batch only on Windows

            "bash" or "sh" =>
                (isWindows ? "bash" : "/bin/bash", "\"{0}\"", ".sh", true),

            "node" or "nodejs" or "javascript" or "js" =>
                ("node", "\"{0}\"", ".js", true),

            "ruby" or "rb" =>
                ("ruby", "\"{0}\"", ".rb", true),

            _ => (string.Empty, string.Empty, string.Empty, false)
        };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n…[truncated — {s.Length:N0} chars total]";

    private static PluginInvocationResult Fail(string msg) =>
        new(false, string.Empty, msg);

    private sealed class ScriptCommand
    {
        public string? Language       { get; set; }
        public string? Code           { get; set; }
        public int?    TimeoutSeconds { get; set; }
    }
}
