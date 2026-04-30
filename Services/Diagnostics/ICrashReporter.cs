namespace VirtmaAi.Services.Diagnostics;

public interface ICrashReporter
{
    string CrashDirectory { get; }
    void Install();
    Task<string> WriteAsync(string source, Exception ex, CancellationToken ct = default);
}
