using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.References;

public interface IReferenceService
{
    Task<IReadOnlyList<Reference>> ListAsync();
    Task<Reference> CreateAsync(Reference reference);
    Task<Reference> UpdateAsync(Reference reference);
    Task DeleteAsync(Guid id);

    Task<IReadOnlyList<Reference>> MatchAsync(string userMessage, CancellationToken ct = default);
    Task<string?> BuildAugmentationAsync(string userMessage, CancellationToken ct = default);

    Task<Reference> RememberAsync(string title, string text, IEnumerable<string>? triggers = null);
}
