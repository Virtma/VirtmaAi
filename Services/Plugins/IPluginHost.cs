using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Plugins;

public interface IPluginHost
{
    Task<IReadOnlyList<Plugin>> ListAsync();
    Task<Plugin?> GetAsync(Guid id);
    Task<Plugin> CreateAsync(Plugin plugin);
    Task<Plugin> UpdateAsync(Plugin plugin);
    Task DeleteAsync(Guid id);

    Task<PluginInvocationResult> InvokeAsync(Guid id, string input, CancellationToken ct = default);

    IReadOnlyList<IBuiltInPlugin> BuiltIn { get; }
    Task<PluginInvocationResult> InvokeBuiltInAsync(string name, string input, CancellationToken ct = default);
}

public sealed record PluginInvocationResult(bool Success, string Output, string? Error = null, int? ExitCode = null);

public interface IBuiltInPlugin
{
    string Name { get; }
    string Description { get; }
    Task<PluginInvocationResult> InvokeAsync(string input, CancellationToken ct);
}
