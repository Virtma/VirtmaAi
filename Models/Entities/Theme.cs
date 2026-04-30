using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("Themes")]
public class Theme
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public string JsonDefinition { get; set; } = "{}";

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
