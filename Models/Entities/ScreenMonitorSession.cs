using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("ScreenMonitorSessions")]
public class ScreenMonitorSession
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Notes { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    public DateTime? EndedAt { get; set; }
}
