namespace VirtmaAi.Services.Media;

public interface IInlineMediaDetector
{
    IReadOnlyList<InlineMedia> Detect(string content);
}

public abstract record InlineMedia;
public sealed record InlineImage(string Url, string? Alt) : InlineMedia;
public sealed record InlineChart(ChartDefinition Chart) : InlineMedia;
