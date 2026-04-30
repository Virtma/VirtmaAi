using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

public enum ReferenceSourceType
{
    File = 0,
    Url = 1,
    Text = 2
}

public enum ReferenceCreator
{
    User = 0,
    Ai = 1
}

[Table("References")]
public class Reference
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    public string Triggers { get; set; } = "[]";

    public ReferenceSourceType SourceType { get; set; }

    public string SourceValue { get; set; } = string.Empty;

    public string? AppliesTo { get; set; }

    public ReferenceCreator CreatedBy { get; set; } = ReferenceCreator.User;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
