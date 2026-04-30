using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("Skills")]
public class Skill
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public string TriggerDescription { get; set; } = string.Empty;

    public string InstructionsMd { get; set; } = string.Empty;

    public string InstructionsHtml { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<SkillContextFile> ContextFiles { get; set; } = new List<SkillContextFile>();

    [NotMapped]
    public bool Enabled { get; set; } = true;
}

[Table("SkillContextFiles")]
public class SkillContextFile
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SkillId { get; set; }

    [ForeignKey(nameof(SkillId))]
    public Skill? Skill { get; set; }

    [MaxLength(1024)]
    public string? FilePath { get; set; }

    public string? Text { get; set; }
}
