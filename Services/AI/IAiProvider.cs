namespace VirtmaAi.Services.AI;

public interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsThinking { get; }

    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default);
}
