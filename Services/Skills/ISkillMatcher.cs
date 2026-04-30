using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Skills;

public interface ISkillMatcher
{
    Task<IReadOnlyList<Skill>> MatchAsync(string userMessage, CancellationToken ct = default);
    Task<string?> BuildAugmentationAsync(string userMessage, CancellationToken ct = default);
}
