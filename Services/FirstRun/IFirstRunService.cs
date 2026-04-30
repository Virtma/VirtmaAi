namespace VirtmaAi.Services.FirstRun;

public interface IFirstRunService
{
    bool IsFirstRun { get; }
    string MarkerPath { get; }
    Task MarkCompleteAsync();

    event EventHandler? Completed;
    void NotifyCompleted();
}
