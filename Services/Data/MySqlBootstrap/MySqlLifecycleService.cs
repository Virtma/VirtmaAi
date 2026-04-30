using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.Data.MySqlBootstrap;

public sealed class MySqlLifecycleService : IMySqlLifecycleService
{
    private readonly ILogger<MySqlLifecycleService> _logger;

    public MySqlLifecycleService(ILogger<MySqlLifecycleService> logger)
    {
        _logger = logger;
    }

#if WINDOWS || MACCATALYST
    public bool IsSupported => true;
#else
    public bool IsSupported => false;
#endif

    public async Task<bool> IsRunningAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            var timeout = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var completed = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
            return completed == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureRunningAsync(MySqlInstall install, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("MySQL lifecycle is desktop-only");

        if (await IsRunningAsync(install.Port, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("MySQL already running on port {Port}", install.Port);
            return;
        }

        var mysqld = ResolveMysqldPath(install.InstallDirectory);
        if (!File.Exists(mysqld))
            throw new FileNotFoundException("mysqld not found", mysqld);

        var psi = new ProcessStartInfo
        {
            FileName = mysqld,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add($"--datadir={install.DataDirectory}");
        psi.ArgumentList.Add($"--port={install.Port}");
        psi.ArgumentList.Add("--console");

        var process = Process.Start(psi);
        if (process == null)
            throw new InvalidOperationException("Failed to launch mysqld");

        _logger.LogInformation("Started mysqld (PID {Pid}) on port {Port}", process.Id, install.Port);
        await WaitForPortAsync(install.Port, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(MySqlInstall install, CancellationToken cancellationToken = default)
    {
        // mysqld on desktop is managed via platform tooling or direct process handle
        // tracked by the lifecycle host. For now we leave it to platform shutdown.
        return Task.CompletedTask;
    }

    private static string ResolveMysqldPath(string installDir)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(installDir, "bin", "mysqld.exe");
        return Path.Combine(installDir, "bin", "mysqld");
    }

    private async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsRunningAsync(port, cancellationToken).ConfigureAwait(false))
                return;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"mysqld did not accept connections on port {port} within {timeout}");
    }
}
