using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("Integrations")]
public class Integration
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(128)]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? AccountIdentifier { get; set; }

    // SecureStorage lookup key for credentials blob
    [MaxLength(256)]
    public string SecureStorageKey { get; set; } = string.Empty;

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
}
