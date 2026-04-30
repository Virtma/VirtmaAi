using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Routines;

public interface IRoutineScheduler : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task RunNowAsync(Guid routineId, CancellationToken ct);
    Task<IReadOnlyList<Routine>> ListAsync();
    Task<Routine> CreateAsync(Routine routine);
    Task<Routine> UpdateAsync(Routine routine);
    Task DeleteAsync(Guid id);

    bool TryDetectRoutineBlock(string message, out string? json);
    Task<Routine> ImportJsonAsync(string json);
    string ExportJson(Routine routine);
}
