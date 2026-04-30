using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.AI;

/// <summary>
/// CRUD for global AI rules. The chat orchestrator pulls enabled rules each turn and prepends
/// them to the system prompt so every model abides by them.
/// </summary>
public interface IAiRulesService
{
    Task<IReadOnlyList<AiRule>> ListAsync(bool enabledOnly = false);
    Task<AiRule> CreateAsync(AiRule rule);
    Task<AiRule> UpdateAsync(AiRule rule);
    Task DeleteAsync(Guid id);
    Task SetEnabledAsync(Guid id, bool enabled);

    /// <summary>
    /// Render enabled rules as a markdown block for system prompts. Sorted by Priority desc, ties
    /// broken by Title. Returns null if no enabled rules exist.
    /// </summary>
    Task<string?> BuildSystemPromptBlockAsync();
}
