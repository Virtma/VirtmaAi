using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("ExternalApiKeys")]
public class ExternalApiKey
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string ProgramName { get; set; } = string.Empty;

    [MaxLength(512)]
    public string HashedKey { get; set; } = string.Empty;

    public string Scopes { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }
}
