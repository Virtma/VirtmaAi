using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Plugins.BuiltIn;

/// <summary>
/// Desktop Commander — gateway to local system operations. Phase 7 ships a safe cross-platform subset:
/// shell exec, list processes, kill process, list network interfaces, system info. More sensitive ops
/// (registry, screenshots, camera/mic streams) are tracked for a per-platform expansion after Phase 17 hardening.
/// </summary>
public sealed class DesktopCommanderPlugin : IBuiltInPlugin
{
    private readonly ILogger<DesktopCommanderPlugin> _logger;

    public DesktopCommanderPlugin(ILogger<DesktopCommanderPlugin> logger)
    {
        _logger = logger;
    }

    public string Name => "desktop-commander";
    public string Description => "Local system operations: shell, processes, network, system info, open URL/file in OS default handler";

    public async Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct)
    {
        DesktopCommand? cmd;
        try { cmd = JsonSerializer.Deserialize<DesktopCommand>(input, JsonOpts); }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, "invalid command json: " + ex.Message); }
        if (cmd is null) return new PluginInvocationResult(false, string.Empty, "empty command");

        return cmd.Action?.ToLowerInvariant() switch
        {
            "shell" => await RunShellAsync(cmd, ct),
            "list-processes" => ListProcesses(),
            "kill-process" => KillProcess(cmd),
            "list-network" => ListNetwork(),
            "system-info" => SystemInfo(),
            "open-url" or "open" or "open-file" => OpenWithDefaultHandler(cmd),
            _ => new PluginInvocationResult(false, string.Empty, "unknown action: " + cmd.Action)
        };
    }

    private static PluginInvocationResult OpenWithDefaultHandler(DesktopCommand cmd)
    {
        var target = cmd.Target ?? cmd.Url ?? cmd.Command;
        if (string.IsNullOrWhiteSpace(target))
            return new PluginInvocationResult(false, string.Empty, "open-url requires 'target' (or 'url'/'command') with a URL or file path");
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            using var p = Process.Start(psi);
            return new PluginInvocationResult(true, $"opened {target} in OS default handler");
        }
        catch (Exception ex)
        {
            return new PluginInvocationResult(false, string.Empty, "open failed: " + ex.Message);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<PluginInvocationResult> RunShellAsync(DesktopCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Command))
            return new PluginInvocationResult(false, string.Empty, "shell requires 'command'");

        // Bound shell exec time so a hung command (network fetch, GUI launch, infinite loop) cannot
        // freeze the chat loop. Caller can override via cmd.TimeoutSeconds.
        var timeout = TimeSpan.FromSeconds(cmd.TimeoutSeconds.GetValueOrDefault(30));
        if (timeout < TimeSpan.FromSeconds(1)) timeout = TimeSpan.FromSeconds(1);
        if (timeout > TimeSpan.FromMinutes(5)) timeout = TimeSpan.FromMinutes(5);

        ProcessStartInfo psi;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi = new ProcessStartInfo("cmd.exe", "/c " + cmd.Command);
        }
        else
        {
            psi = new ProcessStartInfo("/bin/sh", "-c \"" + cmd.Command.Replace("\"", "\\\"") + "\"");
        }
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new PluginInvocationResult(false, string.Empty,
                    $"shell command timed out after {timeout.TotalSeconds:0}s and was killed: " + cmd.Command);
            }

            string stdout = string.Empty;
            string stderr = string.Empty;
            try { stdout = await stdoutTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { stderr = await stderrTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            return new PluginInvocationResult(
                Success: proc.ExitCode == 0,
                Output: stdout,
                Error: string.IsNullOrEmpty(stderr) ? null : stderr,
                ExitCode: proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "shell exec failed");
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private static PluginInvocationResult ListProcesses()
    {
        var sb = new StringBuilder();
        foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName))
        {
            try { sb.Append(p.Id).Append('\t').AppendLine(p.ProcessName); }
            catch { }
        }
        return new PluginInvocationResult(true, sb.ToString());
    }

    private static PluginInvocationResult KillProcess(DesktopCommand cmd)
    {
        if (cmd.Pid is null) return new PluginInvocationResult(false, string.Empty, "kill-process requires 'pid'");
        try
        {
            using var p = Process.GetProcessById(cmd.Pid.Value);
            p.Kill(entireProcessTree: true);
            return new PluginInvocationResult(true, $"killed {cmd.Pid}");
        }
        catch (Exception ex) { return new PluginInvocationResult(false, string.Empty, ex.Message); }
    }

    private static PluginInvocationResult ListNetwork()
    {
        var sb = new StringBuilder();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            sb.Append(ni.Name).Append('\t').Append(ni.NetworkInterfaceType).Append('\t').AppendLine(ni.OperationalStatus.ToString());
        }
        return new PluginInvocationResult(true, sb.ToString());
    }

    private static PluginInvocationResult SystemInfo()
    {
        var info = new
        {
            OS = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            Processors = Environment.ProcessorCount,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            WorkingSet = Environment.WorkingSet
        };
        return new PluginInvocationResult(true, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class DesktopCommand
    {
        public string? Action { get; set; }
        public string? Command { get; set; }
        public string? Target { get; set; }
        public string? Url { get; set; }
        public int? Pid { get; set; }
        public int? TimeoutSeconds { get; set; }
    }
}
