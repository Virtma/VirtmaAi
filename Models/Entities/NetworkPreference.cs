using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("NetworkPreferences")]
public class NetworkPreference
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string? PreferredInterface { get; set; }

    [MaxLength(64)]
    public string? CurrentPublicIp { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
