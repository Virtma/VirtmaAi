namespace VirtmaAi.Services.Data.MySqlBootstrap;

public interface IMySqlLifecycleService
{
    bool IsSupported { get; }
    Task<bool> IsRunningAsync(int port, CancellationToken cancellationToken = default);
    Task EnsureRunningAsync(MySqlInstall install, CancellationToken cancellationToken = default);
    Task StopAsync(MySqlInstall install, CancellationToken cancellationToken = default);
}

public sealed record MySqlInstall(
    string InstallDirectory,
    string DataDirectory,
    int Port,
    string RootPasswordSecureKey);
