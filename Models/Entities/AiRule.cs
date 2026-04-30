using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

/// <summary>
/// A global directive every AI model must follow on every operation. Rules are concatenated into
/// the system prompt for every chat. The user manages them on the AI Rules page.
/// </summary>
[Table("AiRules")]
public class AiRule
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(160)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Plain-text directive (e.g. "Never delete files without asking first.").</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Higher value = appears earlier in the system prompt. Defaults to 100.</summary>
    public int Priority { get; set; } = 100;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
