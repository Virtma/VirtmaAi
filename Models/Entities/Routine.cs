using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

public enum RoutineResponseHandling
{
    Log = 0,
    Notify = 1,
    Email = 2,
    AppendToFile = 3
}

[Table("Routines")]
public class Routine
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(256)]
    public string CronExpression { get; set; } = string.Empty;

    public string Instructions { get; set; } = string.Empty;

    public Guid? ModelId { get; set; }

    public RoutineResponseHandling ResponseHandling { get; set; } = RoutineResponseHandling.Log;

    [MaxLength(1024)]
    public string? ResponseTarget { get; set; }

    public DateTime? LastRunAt { get; set; }

    public DateTime? NextRunAt { get; set; }

    public bool Enabled { get; set; } = true;
}
