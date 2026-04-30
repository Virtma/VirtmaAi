using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace VirtmaAi.Services.FileSystem;

public sealed class GitDiffService : IGitDiffService
{
    private readonly ILogger<GitDiffService> _logger;

    public GitDiffService(ILogger<GitDiffService> logger)
    {
        _logger = logger;
    }

    public bool IsRepository(string path)
    {
        try { return Repository.IsValid(path); }
        catch { return false; }
    }

    public IReadOnlyList<FileDiff> GetWorkingTreeDiff(string repositoryPath)
    {
        if (!IsRepository(repositoryPath)) return Array.Empty<FileDiff>();
        var result = new List<FileDiff>();
        try
        {
            using var repo = new Repository(repositoryPath);
            var options = new CompareOptions
            {
                ContextLines = 3,
                IncludeUnmodified = false
            };

            var patch = repo.Diff.Compare<Patch>(
                repo.Head.Tip?.Tree,
                DiffTargets.WorkingDirectory,
                null,
                null,
                options);

            foreach (var entry in patch)
            {
                var hunks = ParseHunks(entry.Patch);
                result.Add(new FileDiff(entry.Path, Map(entry.Status), hunks));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git diff failed at {Path}", repositoryPath);
        }
        return result;
    }

    private static FileDiffStatus Map(ChangeKind k) => k switch
    {
        ChangeKind.Added => FileDiffStatus.Added,
        ChangeKind.Deleted => FileDiffStatus.Deleted,
        ChangeKind.Modified => FileDiffStatus.Modified,
        ChangeKind.Renamed => FileDiffStatus.Renamed,
        _ => FileDiffStatus.Unknown
    };

    private static IReadOnlyList<DiffHunk> ParseHunks(string patch)
    {
        var hunks = new List<DiffHunk>();
        if (string.IsNullOrEmpty(patch)) return hunks;

        List<DiffLine>? currentLines = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;

        using var reader = new StringReader(patch);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (currentLines is not null)
                    hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, currentLines));
                currentLines = new List<DiffLine>();
                ParseHunkHeader(line, out oldStart, out oldCount, out newStart, out newCount);
                continue;
            }
            if (currentLines is null) continue;

            if (line.StartsWith('+') && !line.StartsWith("+++"))
                currentLines.Add(new DiffLine(DiffLineKind.Added, line[1..]));
            else if (line.StartsWith('-') && !line.StartsWith("---"))
                currentLines.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
            else if (line.StartsWith(' '))
                currentLines.Add(new DiffLine(DiffLineKind.Context, line[1..]));
        }
        if (currentLines is not null)
            hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, currentLines));
        return hunks;
    }

    private static void ParseHunkHeader(string header, out int oldStart, out int oldCount, out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        var m = Regex.Match(header, @"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
        if (!m.Success) return;
        oldStart = int.Parse(m.Groups[1].Value);
        oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
        newStart = int.Parse(m.Groups[3].Value);
        newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
    }
}
