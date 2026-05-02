using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;

namespace VirtmaAi.Services.Plugins;

public sealed class PluginHost : IPluginHost
{
    private readonly IDatabaseService _db;
    private readonly ILogger<PluginHost> _logger;
    private readonly IReadOnlyList<IBuiltInPlugin> _builtIn;

    public PluginHost(IDatabaseService db, ILogger<PluginHost> logger, IEnumerable<IBuiltInPlugin> builtIn)
    {
        _db = db;
        _logger = logger;
        _builtIn = builtIn.ToList();
    }

    public IReadOnlyList<IBuiltInPlugin> BuiltIn => _builtIn;

    public async Task<IReadOnlyList<Plugin>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<Plugin>();
        await using var ctx = _db.CreateContext();
        return await ctx.Plugins.OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Plugin?> GetAsync(Guid id)
    {
        if (_db.Current is null) return null;
        await using var ctx = _db.CreateContext();
        return await ctx.Plugins.FindAsync(id);
    }

    public async Task<Plugin> CreateAsync(Plugin plugin)
    {
        await using var ctx = _db.CreateContext();
        ctx.Plugins.Add(plugin);
        await ctx.SaveChangesAsync();
        return plugin;
    }

    public async Task<Plugin> UpdateAsync(Plugin plugin)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.Plugins.FindAsync(plugin.Id)
            ?? throw new KeyNotFoundException("plugin not found");
        existing.Name = plugin.Name;
        existing.Triggers = plugin.Triggers;
        existing.Instructions = plugin.Instructions;
        existing.ExecutablePath = plugin.ExecutablePath;
        existing.ArgumentsTemplate = plugin.ArgumentsTemplate;
        existing.ResponseParser = plugin.ResponseParser;
        await ctx.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var row = await ctx.Plugins.FindAsync(id);
        if (row is null) return;
        ctx.Plugins.Remove(row);
        await ctx.SaveChangesAsync();
    }

    public async Task<PluginInvocationResult> InvokeAsync(Guid id, string input, CancellationToken ct = default)
    {
        var plugin = await GetAsync(id);
        if (plugin is null) return new PluginInvocationResult(false, string.Empty, "plugin not found");
        if (string.IsNullOrWhiteSpace(plugin.ExecutablePath))
            return new PluginInvocationResult(false, string.Empty, "plugin has no executable configured");

        var args = ExpandArgs(plugin.ArgumentsTemplate, input);
        var psi = new ProcessStartInfo
        {
            FileName = plugin.ExecutablePath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return new PluginInvocationResult(false, string.Empty, "process failed to start");

            if (!string.IsNullOrEmpty(input))
            {
                await proc.StandardInput.WriteAsync(input);
                proc.StandardInput.Close();
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return new PluginInvocationResult(
                Success: proc.ExitCode == 0,
                Output: await stdoutTask,
                Error: string.IsNullOrEmpty(await stderrTask) ? null : await stderrTask,
                ExitCode: proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin invocation failed for {Name}", plugin.Name);
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    public async Task<PluginInvocationResult> InvokeBuiltInAsync(string name, string input, CancellationToken ct = default)
    {
        var plugin = _builtIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (plugin is null) return new PluginInvocationResult(false, string.Empty, "built-in plugin not found: " + name);
        try { return await plugin.InvokeAsync(input, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // user Stop must propagate — don't swallow
        catch (OperationCanceledException ex)
        {
            // Non-user cancellation (e.g. an internal timeout token that escaped the plugin's
            // own catch block) — convert to a clean error result instead of re-throwing.
            _logger.LogWarning(ex, "Built-in plugin {Name} was cancelled by an internal token", name);
            return new PluginInvocationResult(false, string.Empty, $"Plugin timed out or was cancelled internally: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Built-in plugin {Name} threw", name);
            return new PluginInvocationResult(false, string.Empty, ex.Message);
        }
    }

    private static string ExpandArgs(string template, string input)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var sb = new StringBuilder(template);
        sb.Replace("{input}", Escape(input));
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '"', '\t' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
