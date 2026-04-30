using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.AI;

public sealed class ModelDownloadService : IModelDownloadService
{
    // 1 MB read buffer balances throughput against progress granularity.
    private const int BufferSize = 1024 * 1024;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ISettingsService _settings;
    private readonly ILogger<ModelDownloadService> _logger;

    public ModelDownloadService(IHttpClientFactory httpFactory, ISettingsService settings, ILogger<ModelDownloadService> logger)
    {
        _httpFactory = httpFactory;
        _settings = settings;
        _logger = logger;
    }

    public string DefaultModelsDirectory
    {
        get
        {
            var dir = Path.Combine(_settings.DataDirectory, "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public async Task<string> DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("destinationPath required", nameof(destinationPath));

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        // Resume support: if a partial file already exists, send a Range header to pick up where
        // we left off. The user can interrupt + resume long downloads.
        long resumeFrom = 0;
        if (File.Exists(destinationPath))
        {
            try { resumeFrom = new FileInfo(destinationPath).Length; }
            catch { resumeFrom = 0; }
        }

        progress?.Report(new ModelDownloadProgress(resumeFrom, null, null, 0, null, "Connecting…", destinationPath));

        using var client = _httpFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan; // long downloads, governed by ct
        using var req = new HttpRequestMessage(HttpMethod.Get, source);
        if (resumeFrom > 0)
            req.Headers.Range = new global::System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != global::System.Net.HttpStatusCode.PartialContent)
        {
            // Some servers return 200 even with Range — that's fine; treat as fresh.
            resp.EnsureSuccessStatusCode();
        }

        var contentLength = resp.Content.Headers.ContentLength;
        long? totalBytes = contentLength.HasValue ? contentLength + (resp.StatusCode == global::System.Net.HttpStatusCode.PartialContent ? resumeFrom : 0) : null;

        var append = resp.StatusCode == global::System.Net.HttpStatusCode.PartialContent && resumeFrom > 0;

        var fileMode = append ? FileMode.Append : FileMode.Create;
        await using var fs = new FileStream(destinationPath, fileMode, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[BufferSize];
        long downloaded = append ? resumeFrom : 0;
        var sw = Stopwatch.StartNew();
        long lastReportBytes = downloaded;
        long lastReportTicks = sw.ElapsedTicks;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;

            // Throttle progress reports to ~10/s — UI can't usefully render faster.
            var nowTicks = sw.ElapsedTicks;
            var elapsedMs = TimeSpan.FromTicks(nowTicks - lastReportTicks).TotalMilliseconds;
            if (elapsedMs >= 100)
            {
                var sinceBytes = downloaded - lastReportBytes;
                var bps = elapsedMs > 0 ? sinceBytes * 1000.0 / elapsedMs : 0;
                double? percent = totalBytes.HasValue && totalBytes > 0 ? (double)downloaded / totalBytes.Value : null;
                TimeSpan? eta = null;
                if (totalBytes.HasValue && bps > 0)
                {
                    var remaining = totalBytes.Value - downloaded;
                    if (remaining > 0) eta = TimeSpan.FromSeconds(remaining / bps);
                }
                progress?.Report(new ModelDownloadProgress(downloaded, totalBytes, percent, bps, eta, "Downloading", destinationPath));
                lastReportBytes = downloaded;
                lastReportTicks = nowTicks;
            }
        }

        sw.Stop();
        progress?.Report(new ModelDownloadProgress(downloaded, totalBytes ?? downloaded, 1.0, 0, TimeSpan.Zero, "Complete", destinationPath));
        _logger.LogInformation("Downloaded {Bytes} bytes from {Source} to {Dest}", downloaded, source, destinationPath);
        return destinationPath;
    }
}
