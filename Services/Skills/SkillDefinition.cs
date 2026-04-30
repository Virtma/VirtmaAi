using System.Text.Json.Serialization;

namespace VirtmaAi.Services.Skills;

public sealed class SkillDefinition
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "vskill/v1";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled skill";

    [JsonPropertyName("triggerDescription")]
    public string TriggerDescription { get; set; } = string.Empty;

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = string.Empty;

    [JsonPropertyName("contextFiles")]
    public List<string> ContextFiles { get; set; } = new();

    /// <summary>Legacy single-blob context. Kept for import compatibility; new saves split into ContextTexts.</summary>
    [JsonPropertyName("contextText")]
    public string? ContextText { get; set; }

    /// <summary>One entry per text-type context reference. Replaces the old single ContextText blob.</summary>
    [JsonPropertyName("contextTexts")]
    public List<string> ContextTexts { get; set; } = new();
}
