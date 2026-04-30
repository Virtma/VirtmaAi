namespace VirtmaAi.Services.Capture;

public interface ILiveNotetaker
{
    bool IsRecording { get; }
    string? CurrentSessionDirectory { get; }
    Task<string> StartAsync(string? label, TimeSpan interval, CancellationToken ct = default);
    Task StopAsync();
    Task<string> CaptureOnceAsync(string? note, CancellationToken ct = default);
    IReadOnlyList<NoteSession> ListSessions();
}

public sealed record NoteSession(string Id, string Directory, DateTime StartedAtUtc, DateTime? EndedAtUtc, int FrameCount, string? Label);
