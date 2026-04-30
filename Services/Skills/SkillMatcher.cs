using System.Text;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Skills;

public sealed class SkillMatcher : ISkillMatcher
{
    private readonly ISkillRegistry _skills;
    private readonly ILogger<SkillMatcher> _logger;

    public SkillMatcher(ISkillRegistry skills, ILogger<SkillMatcher> logger)
    {
        _skills = skills;
        _logger = logger;
    }

    public async Task<IReadOnlyList<Skill>> MatchAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return Array.Empty<Skill>();
        var enabled = await _skills.ListEnabledAsync();
        if (enabled.Count == 0) return Array.Empty<Skill>();

        var tokens = Tokenize(userMessage);
        var matches = new List<(Skill Skill, int Score)>();
        foreach (var skill in enabled)
        {
            var score = ScoreOverlap(tokens, Tokenize(skill.TriggerDescription));
            if (score > 0) matches.Add((skill, score));
        }
        return matches.OrderByDescending(m => m.Score).Take(5).Select(m => m.Skill).ToList();
    }

    public async Task<string?> BuildAugmentationAsync(string userMessage, CancellationToken ct = default)
    {
        var matched = await MatchAsync(userMessage, ct);
        if (matched.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("# Additional Skills Available");
        sb.AppendLine();
        sb.AppendLine("The user has defined optional skills that provide ADDITIONAL guidance, context, or capabilities for this request. These skills are **purely additive** — they extend what you can do, they do not restrict, override, or disable any of your default abilities, tools, plugins, or integrations. If a skill's instructions conflict with the user's direct request or with your built-in capabilities (such as invoking the Desktop Commander plugin, running tools, or executing tasks), the user's request and your default capabilities take precedence. Use the skills' instructions as helpful augmentation, not as constraints.");
        sb.AppendLine();
        foreach (var skill in matched)
        {
            sb.Append("## Skill: ").AppendLine(skill.Name);
            if (!string.IsNullOrWhiteSpace(skill.TriggerDescription))
                sb.Append("_When relevant:_ ").AppendLine(skill.TriggerDescription);
            sb.AppendLine();
            sb.AppendLine(skill.InstructionsMd);
            foreach (var ctxFile in skill.ContextFiles)
            {
                if (!string.IsNullOrEmpty(ctxFile.Text))
                {
                    sb.AppendLine();
                    // When a file was attached, show its name so the AI knows the source.
                    if (!string.IsNullOrEmpty(ctxFile.FilePath))
                    {
                        sb.Append("**Reference file: ")
                          .Append(Path.GetFileName(ctxFile.FilePath))
                          .AppendLine("**");
                    }
                    sb.AppendLine(ctxFile.Text);
                }
                else if (!string.IsNullOrEmpty(ctxFile.FilePath))
                {
                    // Content was not extracted (file unreadable or not yet synced).
                    sb.Append("_Context file (content unavailable):_ ").AppendLine(ctxFile.FilePath);
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(s)) return set;
        foreach (var raw in s.Split(new[] { ' ', ',', '.', ';', ':', '\t', '\n', '\r', '/', '\\', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim().ToLowerInvariant();
            if (w.Length >= 3) set.Add(w);
        }
        return set;
    }

    private static int ScoreOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var small = a.Count <= b.Count ? a : b;
        var large = a.Count <= b.Count ? b : a;
        var score = 0;
        foreach (var w in small) if (large.Contains(w)) score++;
        return score;
    }
}
