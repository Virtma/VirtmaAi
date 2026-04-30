using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Diagnostics;

public sealed class CrashReporter : ICrashReporter
{
    private const string EventSourceName = "VirtmaAi";
    private const string EventLogName = "Application";

    private readonly ISettingsService _settings;
    private readonly ILogger<CrashReporter> _logger;
    private int _installed;

    public CrashReporter(ISettingsService settings, ILogger<CrashReporter> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public string CrashDirectory
    {
        get
        {
            var baseDir = string.IsNullOrWhiteSpace(_settings.DataDirectory) ? Microsoft.Maui.Storage.FileSystem.AppDataDirectory : _settings.DataDirectory;
            return Path.Combine(baseDir, "crashes");
        }
    }

    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                _ = WriteAsync("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _ = WriteAsync("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        _logger.LogInformation("Crash reporter installed. Dumps directory: {Dir}", CrashDirectory);
    }

    public async Task<string> WriteAsync(string source, Exception ex, CancellationToken ct = default)
    {
        var file = string.Empty;
        try
        {
            Directory.CreateDirectory(CrashDirectory);
            file = Path.Combine(CrashDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            var sb = new StringBuilder();
            sb.AppendLine("VirtmaAi crash report");
            sb.AppendLine($"Timestamp (UTC): {DateTime.UtcNow:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"Runtime: {Environment.Version}  OS: {Environment.OSVersion}");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(ex.ToString());
            var body = sb.ToString();
            await File.WriteAllTextAsync(file, body, ct).ConfigureAwait(false);
            _logger.LogError(ex, "Crash captured at {File}", file);

#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                TryWriteWindowsEventLog(source, body, file);
            }
#endif
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Crash reporter failed to write dump");
        }
        return file;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private void TryWriteWindowsEventLog(string source, string body, string file)
    {
        try
        {
            var summary = $"VirtmaAi crash ({source}). Dump: {file}\n\n{Truncate(body, 16_000)}";
            using var log = new global::System.Diagnostics.EventLog(EventLogName)
            {
                Source = global::System.Diagnostics.EventLog.SourceExists(EventSourceName) ? EventSourceName : EventLogName
            };
            log.WriteEntry(summary, global::System.Diagnostics.EventLogEntryType.Error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Windows Event Log write skipped (no permissions or unavailable)");
        }
    }
#endif

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "\n... [truncated]";
}
