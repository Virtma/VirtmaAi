using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Graphify;

public sealed class GraphifyService : IGraphifyService
{
    private readonly IGraphifyRuntime _runtime;
    private readonly ISettingsService _settings;
    private readonly IDatabaseService _db;
    private readonly ILogger<GraphifyService> _logger;

    public GraphifyService(IGraphifyRuntime runtime, ISettingsService settings, IDatabaseService db, ILogger<GraphifyService> logger)
    {
        _runtime = runtime;
        _settings = settings;
        _db = db;
        _logger = logger;
    }

    public async Task<GraphifyGraph> GenerateAsync(string projectDir, Guid? conversationId, IProgress<GraphifyInstallProgress>? progress, CancellationToken ct)
    {
        if (!_runtime.IsDesktop) throw new PlatformNotSupportedException("Graphify requires desktop");
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            throw new DirectoryNotFoundException("project dir not found: " + projectDir);

        var status = await _runtime.EnsureInstalledAsync(progress, ct);
        if (!status.GraphifyInstalled)
            throw new InvalidOperationException("Graphify runtime not ready: " + (status.Error ?? "install failed"));

        var graphId = Guid.NewGuid();
        var outDir = Path.Combine(_settings.DataDirectory, "graphify", graphId.ToString("N"));
        Directory.CreateDirectory(outDir);

        progress?.Report(new GraphifyInstallProgress("Running graphify"));
        var code = await RunAsync(_runtime.UvExecutable,
            $"tool run graphifyy \"{projectDir}\" --output \"{outDir}\" --no-viz", ct);
        if (code != 0) throw new InvalidOperationException("graphify exited with " + code);

        var graphJson = Path.Combine(outDir, "graph.json");
        var report = Path.Combine(outDir, "GRAPH_REPORT.md");
        if (!File.Exists(graphJson))
            throw new FileNotFoundException("graphify produced no graph.json", graphJson);

        var entity = new GraphifyGraph
        {
            Id = graphId,
            ConversationId = conversationId,
            ProjectDir = projectDir,
            GraphJsonPath = graphJson,
            ReportMdPath = File.Exists(report) ? report : string.Empty
        };
        await using var dbCtx = _db.CreateContext();
        dbCtx.GraphifyGraphs.Add(entity);
        await dbCtx.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<IReadOnlyList<GraphifyGraph>> ListAsync(Guid? conversationId = null)
    {
        if (_db.Current is null) return Array.Empty<GraphifyGraph>();
        await using var ctx = _db.CreateContext();
        var q = ctx.GraphifyGraphs.AsQueryable();
        if (conversationId is not null) q = q.Where(g => g.ConversationId == conversationId);
        return await q.OrderByDescending(g => g.CreatedAt).ToListAsync();
    }

    public async Task<GraphifyGraph?> GetAsync(Guid id)
    {
        if (_db.Current is null) return null;
        await using var ctx = _db.CreateContext();
        return await ctx.GraphifyGraphs.FindAsync(id);
    }

    public async Task DeleteAsync(Guid id)
    {
        var g = await GetAsync(id);
        if (g is null) return;
        await using var ctx = _db.CreateContext();
        ctx.GraphifyGraphs.Remove(g);
        await ctx.SaveChangesAsync();
        try { if (Directory.Exists(Path.GetDirectoryName(g.GraphJsonPath) ?? string.Empty))
                Directory.Delete(Path.GetDirectoryName(g.GraphJsonPath)!, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "cleanup graph files"); }
    }

    public async Task<string?> ReadReportAsync(Guid id, CancellationToken ct = default)
    {
        var g = await GetAsync(id);
        if (g is null || string.IsNullOrEmpty(g.ReportMdPath) || !File.Exists(g.ReportMdPath)) return null;
        return await File.ReadAllTextAsync(g.ReportMdPath, ct);
    }

    public async Task<string?> ReadGraphJsonAsync(Guid id, CancellationToken ct = default)
    {
        var g = await GetAsync(id);
        if (g is null || !File.Exists(g.GraphJsonPath)) return null;
        return await File.ReadAllTextAsync(g.GraphJsonPath, ct);
    }

    private static async Task<int> RunAsync(string exe, string args, CancellationToken ct)
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
