using System.Text.Json;
using System.Text.RegularExpressions;

namespace VirtmaAi.Services.Media;

public sealed partial class InlineMediaDetector : IInlineMediaDetector
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"```(?:vchart|chart)\s*\n(?<body>[\s\S]*?)```", RegexOptions.Compiled)]
    private static partial Regex ChartRegex();

    public IReadOnlyList<InlineMedia> Detect(string content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<InlineMedia>();
        var list = new List<InlineMedia>();

        foreach (Match m in ImageRegex().Matches(content))
        {
            var alt = m.Groups[1].Value;
            var url = m.Groups[2].Value.Trim();
            if (LooksLikeImageUrl(url)) list.Add(new InlineImage(url, string.IsNullOrWhiteSpace(alt) ? null : alt));
        }

        foreach (Match m in ChartRegex().Matches(content))
        {
            var body = m.Groups["body"].Value;
            if (string.IsNullOrWhiteSpace(body)) continue;
            try
            {
                var def = JsonSerializer.Deserialize<ChartDefinition>(body, JsonOpts);
                if (def is not null) list.Add(new InlineChart(def));
            }
            catch { }
        }

        return list;
    }

    private static bool LooksLikeImageUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        if (lower.StartsWith("data:image/")) return true;
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" })
            if (lower.Contains(ext)) return true;
        return lower.StartsWith("http://") || lower.StartsWith("https://");
    }
}
