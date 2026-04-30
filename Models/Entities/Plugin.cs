using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("Plugins")]
public class Plugin
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    // JSON array of trigger strings
    public string Triggers { get; set; } = "[]";

    public string Instructions { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? ExecutablePath { get; set; }

    public string ArgumentsTemplate { get; set; } = string.Empty;

    public string? ResponseParser { get; set; }
}
