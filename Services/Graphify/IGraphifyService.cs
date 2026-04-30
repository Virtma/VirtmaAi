using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Graphify;

public interface IGraphifyService
{
    Task<GraphifyGraph> GenerateAsync(
        string projectDir,
        Guid? conversationId,
        IProgress<GraphifyInstallProgress>? progress,
        CancellationToken ct);

    Task<IReadOnlyList<GraphifyGraph>> ListAsync(Guid? conversationId = null);
    Task<GraphifyGraph?> GetAsync(Guid id);
    Task DeleteAsync(Guid id);

    Task<string?> ReadReportAsync(Guid id, CancellationToken ct = default);
    Task<string?> ReadGraphJsonAsync(Guid id, CancellationToken ct = default);
}
