using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Settings;

namespace VirtmaAi.Services.Routines;

public sealed class RoutineScheduler : IRoutineScheduler
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Regex RoutineBlockRegex = new(
        @"```(?:json|vroutine)?\s*(\{[\s\S]*?""schema""\s*:\s*""vroutine/v1""[\s\S]*?\})\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IDatabaseService _db;
    private readonly IProviderRouter _router;
    private readonly ISettingsService _settings;
    private readonly ILogger<RoutineScheduler> _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public RoutineScheduler(IDatabaseService db, IProviderRouter router, ISettingsService settings, ILogger<RoutineScheduler> logger)
    {
        _db = db;
        _router = router;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _loopCts?.Cancel(); if (_loop is not null) await _loop; }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "routine tick"); }

            var now = DateTime.UtcNow;
            var delay = TimeSpan.FromSeconds(60 - now.Second);
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_db.Current is null) return;
        var now = DateTime.UtcNow;
        List<Routine> due;
        await using (var readCtx = _db.CreateContext())
        {
            due = await readCtx.Routines
                .Where(r => r.Enabled)
                .ToListAsync(ct);
        }
        foreach (var routine in due)
        {
            if (string.IsNullOrWhiteSpace(routine.CronExpression)) continue;
            try
            {
                var cron = new CronSchedule(routine.CronExpression);
                if (!cron.Matches(now)) continue;
                if (routine.LastRunAt is { } last && (now - last) < TimeSpan.FromSeconds(50)) continue;
                await ExecuteAsync(routine, ct);
            }
            catch (Exception ex) { _logger.LogError(ex, "routine {Name}", routine.Name); }
        }
    }

    public async Task RunNowAsync(Guid routineId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var routine = await ctx.Routines.FindAsync(routineId);
        if (routine is null) return;
        await ExecuteAsync(routine, ct);
    }

    private async Task ExecuteAsync(Routine routine, CancellationToken ct)
    {
        var provider = _router.All.FirstOrDefault() ?? throw new InvalidOperationException("no AI providers");
        var request = new ChatRequest("default", new[] { new ChatMessage(ChatRole.User, routine.Instructions) });

        var sb = new StringBuilder();
        await foreach (var evt in provider.StreamAsync(request, ct))
        {
            if (evt is ContentChunk c) sb.Append(c.Text);
            else if (evt is StreamError err) _logger.LogWarning("routine {Name} stream error: {Msg}", routine.Name, err.Message);
        }
        var output = sb.ToString();
        await HandleOutputAsync(routine, output, ct);

        await using var ctx = _db.CreateContext();
        var fresh = await ctx.Routines.FindAsync(routine.Id);
        if (fresh is not null)
        {
            fresh.LastRunAt = DateTime.UtcNow;
            try { fresh.NextRunAt = new CronSchedule(fresh.CronExpression).NextAfter(DateTime.UtcNow); }
            catch { fresh.NextRunAt = null; }
            await ctx.SaveChangesAsync(ct);
        }
    }

    private async Task HandleOutputAsync(Routine routine, string output, CancellationToken ct)
    {
        switch (routine.ResponseHandling)
        {
            case RoutineResponseHandling.Log:
                _logger.LogInformation("Routine {Name} output: {Output}", routine.Name, output);
                break;
            case RoutineResponseHandling.AppendToFile:
                var path = routine.ResponseTarget
                    ?? Path.Combine(_settings.DataDirectory, "routines", routine.Name + ".log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.AppendAllTextAsync(path, $"\n---\n[{DateTime.UtcNow:u}]\n{output}\n", ct);
                break;
            default:
                _logger.LogInformation("Routine {Name} produced output (handler {Handler} not yet implemented): {Output}", routine.Name, routine.ResponseHandling, output);
                break;
        }
    }

    public async Task<IReadOnlyList<Routine>> ListAsync()
    {
        if (_db.Current is null) return Array.Empty<Routine>();
        await using var ctx = _db.CreateContext();
        return await ctx.Routines.OrderBy(r => r.Name).ToListAsync();
    }

    public async Task<Routine> CreateAsync(Routine routine)
    {
        await using var ctx = _db.CreateContext();
        try { routine.NextRunAt = new CronSchedule(routine.CronExpression).NextAfter(DateTime.UtcNow); }
        catch { routine.NextRunAt = null; }
        ctx.Routines.Add(routine);
        await ctx.SaveChangesAsync();
        return routine;
    }

    public async Task<Routine> UpdateAsync(Routine routine)
    {
        await using var ctx = _db.CreateContext();
        var existing = await ctx.Routines.FindAsync(routine.Id)
            ?? throw new KeyNotFoundException("routine not found");
        existing.Name = routine.Name;
        existing.CronExpression = routine.CronExpression;
        existing.Instructions = routine.Instructions;
        existing.ResponseHandling = routine.ResponseHandling;
        existing.ResponseTarget = routine.ResponseTarget;
        existing.Enabled = routine.Enabled;
        try { existing.NextRunAt = new CronSchedule(existing.CronExpression).NextAfter(DateTime.UtcNow); }
        catch { existing.NextRunAt = null; }
        await ctx.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var ctx = _db.CreateContext();
        var row = await ctx.Routines.FindAsync(id);
        if (row is null) return;
        ctx.Routines.Remove(row);
        await ctx.SaveChangesAsync();
    }

    public bool TryDetectRoutineBlock(string message, out string? json)
    {
        json = null;
        if (string.IsNullOrWhiteSpace(message)) return false;
        var m = RoutineBlockRegex.Match(message);
        if (m.Success) { json = m.Groups[1].Value; return true; }
        return false;
    }

    public Task<Routine> ImportJsonAsync(string json)
    {
        var def = JsonSerializer.Deserialize<RoutineDefinition>(json, JsonOpts)
            ?? throw new InvalidDataException("routine json did not parse");
        if (!string.Equals(def.Schema, "vroutine/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("unsupported routine schema");
        return Task.FromResult(new Routine
        {
            Name = def.Name,
            CronExpression = def.CronExpression,
            Instructions = def.Instructions,
            ResponseHandling = def.ResponseHandling,
            ResponseTarget = def.ResponseTarget,
            Enabled = def.Enabled
        });
    }

    public string ExportJson(Routine r) => JsonSerializer.Serialize(new RoutineDefinition
    {
        Name = r.Name,
        CronExpression = r.CronExpression,
        Instructions = r.Instructions,
        ResponseHandling = r.ResponseHandling,
        ResponseTarget = r.ResponseTarget,
        Enabled = r.Enabled
    }, JsonOpts);

    private sealed class RoutineDefinition
    {
        public string Schema { get; set; } = "vroutine/v1";
        public string Name { get; set; } = string.Empty;
        public string CronExpression { get; set; } = string.Empty;
        public string Instructions { get; set; } = string.Empty;
        public RoutineResponseHandling ResponseHandling { get; set; } = RoutineResponseHandling.Log;
        public string? ResponseTarget { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
