using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("GraphifyGraphs")]
public class GraphifyGraph
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ConversationId { get; set; }

    [MaxLength(1024)]
    public string? ProjectDir { get; set; }

    [MaxLength(1024)]
    public string GraphJsonPath { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string ReportMdPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
