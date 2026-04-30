namespace VirtmaAi.Services.AI;

public interface IProviderRouter
{
    IAiProvider Get(string providerId);
    IReadOnlyCollection<IAiProvider> All { get; }
}

public sealed class ProviderRouter : IProviderRouter
{
    private readonly Dictionary<string, IAiProvider> _byId;

    public ProviderRouter(IEnumerable<IAiProvider> providers)
    {
        _byId = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IAiProvider Get(string providerId)
    {
        if (!_byId.TryGetValue(providerId, out var provider))
            throw new InvalidOperationException($"Unknown provider: {providerId}");
        return provider;
    }

    public IReadOnlyCollection<IAiProvider> All => _byId.Values;
}
