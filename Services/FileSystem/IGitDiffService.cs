namespace VirtmaAi.Services.FileSystem;

public interface IGitDiffService
{
    bool IsRepository(string path);
    IReadOnlyList<FileDiff> GetWorkingTreeDiff(string repositoryPath);
}

public sealed record FileDiff(
    string Path,
    FileDiffStatus Status,
    IReadOnlyList<DiffHunk> Hunks);

public enum FileDiffStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Unknown
}

public sealed record DiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<DiffLine> Lines);

public sealed record DiffLine(DiffLineKind Kind, string Text);

public enum DiffLineKind
{
    Context,
    Added,
    Removed
}
