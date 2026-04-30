using System.Text.Json.Serialization;

namespace VirtmaAi.Services.Media;

public sealed class ChartDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "bar";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("data")]
    public List<ChartSeries> Data { get; set; } = new();

    [JsonPropertyName("axes")]
    public ChartAxes? Axes { get; set; }
}

public sealed class ChartSeries
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("values")]
    public List<double> Values { get; set; } = new();

    [JsonPropertyName("categories")]
    public List<string>? Categories { get; set; }

    [JsonPropertyName("color")]
    public string? Color { get; set; }
}

public sealed class ChartAxes
{
    [JsonPropertyName("x")]
    public string? X { get; set; }

    [JsonPropertyName("y")]
    public string? Y { get; set; }
}
