using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Data.MySqlBootstrap;

public sealed class MySqlProvisioner : IDatabaseProvisioner
{
    public const int DefaultPort = 33060;
    public const string RootPasswordSecureKey = "virtmaai.mysql.root_password";
    public const string DatabaseName = "virtmaai";

    private readonly ISettingsService _settings;
    private readonly IMySqlLifecycleService _lifecycle;
    private readonly ILogger<MySqlProvisioner> _logger;

    public MySqlProvisioner(
        ISettingsService settings,
        IMySqlLifecycleService lifecycle,
        ILogger<MySqlProvisioner> logger)
    {
        _settings = settings;
        _lifecycle = lifecycle;
        _logger = logger;
    }

    public DatabaseKind Kind => DatabaseKind.MySql;

    public async Task<DatabaseConnectionInfo> EnsureProvisionedAsync(
        IProgress<ProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_lifecycle.IsSupported)
            throw new PlatformNotSupportedException("MySQL provisioning is not supported on this platform");

        var port = DefaultPort;

        progress?.Report(new ProvisionProgress("Checking for existing MySQL instance"));
        var alreadyRunning = await _lifecycle.IsRunningAsync(port, cancellationToken).ConfigureAwait(false);

        string? rootPassword = await _settings.GetSecretAsync(RootPasswordSecureKey).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootPassword))
        {
            rootPassword = GenerateRandomPassword();
            await _settings.SetSecretAsync(RootPasswordSecureKey, rootPassword).ConfigureAwait(false);
        }

        if (!alreadyRunning)
        {
            progress?.Report(new ProvisionProgress("MySQL not detected on port " + port));
            _logger.LogInformation(
                "No MySQL detected on port {Port} — defer to first-run installer flow or fall back to SQLite",
                port);

            throw new MySqlNotInstalledException(
                $"MySQL was not detected on localhost:{port}. Run the first-run installer or fall back to SQLite.");
        }

        progress?.Report(new ProvisionProgress("Ensuring virtmaai schema exists"));
        var serverConn = $"Server=127.0.0.1;Port={port};Uid=root;Pwd={rootPassword};SslMode=None;AllowPublicKeyRetrieval=True";
        // Schema creation is performed by EF Core migrations once the connection string is returned.
        var appConn = serverConn + $";Database={DatabaseName}";
        progress?.Report(new ProvisionProgress("MySQL ready", 1.0));
        return new DatabaseConnectionInfo(DatabaseKind.MySql, appConn);
    }

    private static string GenerateRandomPassword()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', 'a').Replace('/', 'b').Replace('=', 'c');
    }
}

public sealed class MySqlNotInstalledException : Exception
{
    public MySqlNotInstalledException(string message) : base(message) { }
}
