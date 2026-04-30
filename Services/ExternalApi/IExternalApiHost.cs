namespace VirtmaAi.Services.ExternalApi;

public interface IExternalApiHost : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }
    Task StartAsync(int port, CancellationToken ct = default);
    Task StopAsync();
}
