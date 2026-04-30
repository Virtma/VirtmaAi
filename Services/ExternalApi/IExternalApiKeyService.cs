using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.ExternalApi;

public interface IExternalApiKeyService
{
    Task<IReadOnlyList<ExternalApiKey>> ListAsync();
    Task<(ExternalApiKey Record, string PlainKey)> IssueAsync(string programName, IEnumerable<string>? scopes = null);
    Task RevokeAsync(Guid id);
    Task<ExternalApiKey?> VerifyAsync(string plainKey);
}
