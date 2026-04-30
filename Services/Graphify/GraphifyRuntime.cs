using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Graphify;

/// <summary>
/// Manages a bundled portable Python + uv runtime for Graphify. Phase 9 ships the lifecycle
/// and probe logic; the portable runtime bundle itself is stored under the user's data directory.
/// On mobile platforms <see cref="IsDesktop"/> is false and Graphify features stay hidden.
/// </summary>
public sealed class GraphifyRuntime : IGraphifyRuntime
{
    // python-build-standalone "latest" release index. The release tag changes monthly; we resolve
    // the asset URL dynamically against this fixed-shape API so the download URL stays valid as
    // upstream cuts new releases.
    private const string PbsApiLatest = "https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest";
    private const string UvApiLatest = "https://api.github.com/repos/astral-sh/uv/releases/latest";

    private readonly ISettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GraphifyRuntime> _logger;

    public GraphifyRuntime(ISettingsService settings, IHttpClientFactory httpFactory, ILogger<GraphifyRuntime> logger)
    {
        _settings = settings;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public bool IsDesktop =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public string RuntimeRoot => Path.Combine(_settings.DataDirectory, "python");
    public string PythonExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(RuntimeRoot, "python", "python.exe")
        : Path.Combine(RuntimeRoot, "python", "bin", "python3");
    public string UvExecutable => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(RuntimeRoot, "uv", "uv.exe")
        : Path.Combine(RuntimeRoot, "uv", "uv");

    public GraphifyRuntimeStatus Probe()
    {
        if (!IsDesktop)
            return new GraphifyRuntimeStatus(false, false, null, null, "Graphify requires desktop");

        var pythonReady = File.Exists(PythonExecutable);
        var uvReady = File.Exists(UvExecutable);
        if (!pythonReady || !uvReady)
            return new GraphifyRuntimeStatus(false, false, null, null, "Runtime not installed");

        string? pyVersion = TryRunCapture(PythonExecutable, "--version");
        string? gVersion = TryRunCapture(UvExecutable, "tool run graphifyy --version");
        var installed = !string.IsNullOrWhiteSpace(gVersion);
        return new GraphifyRuntimeStatus(true, installed, pyVersion?.Trim(), gVersion?.Trim(), null);
    }

    public async Task<GraphifyRuntimeStatus> EnsureInstalledAsync(IProgress<GraphifyInstallProgress>? progress, CancellationToken ct)
    {
        if (!IsDesktop)
            return new GraphifyRuntimeStatus(false, false, null, null, "Graphify requires desktop");

        progress?.Report(new GraphifyInstallProgress("Checking runtime"));
        Directory.CreateDirectory(RuntimeRoot);

        if (!File.Exists(PythonExecutable))
        {
            try
            {
                await DownloadPortablePythonAsync(progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "portable python download failed");
                return new GraphifyRuntimeStatus(false, false, null, null,
                    "Portable Python download failed: " + ex.Message);
            }
            if (!File.Exists(PythonExecutable))
                return new GraphifyRuntimeStatus(false, false, null, null,
                    "Portable Python download completed but executable wasn't found at " + PythonExecutable);
        }
        if (!File.Exists(UvExecutable))
        {
            try
            {
                await DownloadUvAsync(progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "uv download failed");
                return new GraphifyRuntimeStatus(true, false, null, null,
                    "uv download failed: " + ex.Message);
            }
            if (!File.Exists(UvExecutable))
                return new GraphifyRuntimeStatus(true, false, null, null,
                    "uv download completed but executable wasn't found at " + UvExecutable);
        }

        progress?.Report(new GraphifyInstallProgress("Installing graphify via uv"));
        var code = await RunAsync(UvExecutable, "tool install graphifyy", ct);
        if (code != 0)
            return new GraphifyRuntimeStatus(true, false, null, null, "uv tool install failed (exit " + code + ")");

        progress?.Report(new GraphifyInstallProgress("Complete", 1.0));
        return Probe();
    }

    private async Task DownloadPortablePythonAsync(IProgress<GraphifyInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new GraphifyInstallProgress("Locating python-build-standalone release"));
        var url = await ResolvePbsAssetAsync(ct).ConfigureAwait(false);
        if (url is null) throw new InvalidOperationException("could not find a python-build-standalone asset for this platform");

        Directory.CreateDirectory(RuntimeRoot);
        var archive = Path.Combine(RuntimeRoot, "python-download" + GuessArchiveExt(url));

        // Skip download if a previous run already pulled the archive but failed mid-extract.
        if (!File.Exists(archive))
        {
            progress?.Report(new GraphifyInstallProgress("Downloading Python", Detail: url));
            await DownloadWithProgressAsync(url, archive, "Python", progress, ct).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new GraphifyInstallProgress("Reusing existing Python download"));
        }

        progress?.Report(new GraphifyInstallProgress("Extracting Python"));
        // python-build-standalone install_only archives have a top-level "python/" folder.
        // Extract into RuntimeRoot so the result is RuntimeRoot/python/{python.exe,bin/python3}.
        // Wipe any stale partial extract first.
        var pythonDir = Path.Combine(RuntimeRoot, "python");
        if (Directory.Exists(pythonDir))
        {
            try { Directory.Delete(pythonDir, recursive: true); } catch { }
        }
        ExtractArchive(archive, RuntimeRoot);

        if (!File.Exists(PythonExecutable))
        {
            // Fall back: the archive used a different top-level name. Search for python.exe /
            // python3 anywhere in RuntimeRoot and move its parent to the expected location.
            RelocateToCanonical(RuntimeRoot, "python", new[] { "python.exe", Path.Combine("bin", "python3") });
        }

        try { File.Delete(archive); } catch { }
    }

    private async Task DownloadUvAsync(IProgress<GraphifyInstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new GraphifyInstallProgress("Locating uv release"));
        var url = await ResolveUvAssetAsync(ct).ConfigureAwait(false);
        if (url is null) throw new InvalidOperationException("could not find a uv asset for this platform");

        Directory.CreateDirectory(RuntimeRoot);
        var archive = Path.Combine(RuntimeRoot, "uv-download" + GuessArchiveExt(url));

        if (!File.Exists(archive))
        {
            progress?.Report(new GraphifyInstallProgress("Downloading uv", Detail: url));
            await DownloadWithProgressAsync(url, archive, "uv", progress, ct).ConfigureAwait(false);
        }
        else
        {
            progress?.Report(new GraphifyInstallProgress("Reusing existing uv download"));
        }

        progress?.Report(new GraphifyInstallProgress("Extracting uv"));
        var uvDir = Path.Combine(RuntimeRoot, "uv");
        if (Directory.Exists(uvDir))
        {
            try { Directory.Delete(uvDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(uvDir);
        ExtractArchive(archive, uvDir);

        // uv tarballs (Linux/Mac) often include a "uv-x86_64-...-linux-gnu/" subdir that holds
        // the binary. The Windows zip extracts uv.exe at root. Normalize either layout to
        // RuntimeRoot/uv/uv{.exe}.
        if (!File.Exists(UvExecutable))
            RelocateToCanonical(RuntimeRoot, "uv", new[] { "uv.exe", "uv" });

        try { File.Delete(archive); } catch { }
    }

    /// <summary>
    /// Search <paramref name="searchRoot"/> recursively for any of <paramref name="markerFiles"/>
    /// and move the directory containing the marker so it ends up at
    /// <c>{searchRoot}/{canonicalName}</c>. No-op if the canonical layout is already correct.
    /// </summary>
    private static void RelocateToCanonical(string searchRoot, string canonicalName, string[] markerFiles)
    {
        var canonical = Path.Combine(searchRoot, canonicalName);
        foreach (var marker in markerFiles)
        {
            var direct = Path.Combine(canonical, marker);
            if (File.Exists(direct)) return; // already canonical
        }

        foreach (var marker in markerFiles)
        {
            string[] hits;
            try { hits = Directory.GetFiles(searchRoot, Path.GetFileName(marker), SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var hit in hits)
            {
                // Walk up to find the directory whose suffix matches the marker (e.g. for
                // "bin/python3", walk to the dir whose `bin/python3` file equals `hit`).
                var sep = Path.DirectorySeparatorChar;
                var depth = marker.Count(c => c == sep || c == '/' || c == '\\');
                var dir = Path.GetDirectoryName(hit);
                for (int i = 0; i < depth && !string.IsNullOrEmpty(dir); i++)
                    dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) continue;
                if (string.Equals(dir, canonical, StringComparison.OrdinalIgnoreCase)) return;

                try
                {
                    if (Directory.Exists(canonical)) Directory.Delete(canonical, recursive: true);
                    Directory.Move(dir, canonical);
                    return;
                }
                catch { }
            }
        }
    }

    private async Task<string?> ResolvePbsAssetAsync(CancellationToken ct)
    {
        // python-build-standalone ships per-platform "install_only" tarballs, e.g.
        // cpython-3.12.5+20240814-x86_64-pc-windows-msvc-install_only.tar.gz
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => "x86_64"
        };
        string targetSubstr;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) targetSubstr = $"{arch}-pc-windows-msvc-install_only";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) targetSubstr = $"{arch}-apple-darwin-install_only";
        else targetSubstr = $"{arch}-unknown-linux-gnu-install_only";

        var assets = await GetReleaseAssetsAsync(PbsApiLatest, ct).ConfigureAwait(false);
        // Prefer .tar.gz; fall back to anything matching.
        return assets.FirstOrDefault(a => a.Contains(targetSubstr, StringComparison.OrdinalIgnoreCase) && a.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
               ?? assets.FirstOrDefault(a => a.Contains(targetSubstr, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveUvAssetAsync(CancellationToken ct)
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            _ => "x86_64"
        };
        string targetSubstr;
        string ext;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { targetSubstr = $"{arch}-pc-windows-msvc"; ext = ".zip"; }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { targetSubstr = $"{arch}-apple-darwin"; ext = ".tar.gz"; }
        else { targetSubstr = $"{arch}-unknown-linux-gnu"; ext = ".tar.gz"; }

        var assets = await GetReleaseAssetsAsync(UvApiLatest, ct).ConfigureAwait(false);
        return assets.FirstOrDefault(a => a.Contains(targetSubstr, StringComparison.OrdinalIgnoreCase) && a.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<string>> GetReleaseAssetsAsync(string apiUrl, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VirtmaAi-Graphify/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var urls = new List<string>();
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String)
                    urls.Add(u.GetString()!);
            }
        }
        return urls;
    }

    private async Task DownloadWithProgressAsync(string url, string destination, string label, IProgress<GraphifyInstallProgress>? progress, CancellationToken ct)
    {
        using var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VirtmaAi-Graphify/1.0");
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(destination);
        var buf = new byte[1024 * 256];
        long downloaded = 0;
        var sw = Stopwatch.StartNew();
        long lastReport = 0;
        while (true)
        {
            var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            downloaded += n;
            if (sw.ElapsedMilliseconds - lastReport > 250)
            {
                lastReport = sw.ElapsedMilliseconds;
                double? pct = total.HasValue && total.Value > 0 ? (double)downloaded / total.Value : null;
                var msg = $"Downloading {label} — {Mb(downloaded):0.0} / {(total.HasValue ? Mb(total.Value).ToString("0.0") : "?")} MB";
                progress?.Report(new GraphifyInstallProgress(msg, pct));
            }
        }
    }

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    private static string GuessArchiveExt(string url)
    {
        if (url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ".tgz";
        if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ".zip";
        return Path.GetExtension(url);
    }

    private static void ExtractArchive(string archive, string destination)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }
        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGz(archive, destination);
            return;
        }
        throw new NotSupportedException("Unsupported archive type: " + archive);
    }

    private static void ExtractTarGz(string archive, string destination)
    {
        using var fs = File.OpenRead(archive);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        // .NET 7+ has TarFile.ExtractToDirectory
        global::System.Formats.Tar.TarFile.ExtractToDirectory(gz, destination, overwriteFiles: true);
    }


    private string? TryRunCapture(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(10000)) { p.Kill(); return null; }
            return p.ExitCode == 0 ? p.StandardOutput.ReadToEnd() : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "probe run failed: {Exe} {Args}", exe, args);
            return null;
        }
    }

    private async Task<int> RunAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return -1;
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
