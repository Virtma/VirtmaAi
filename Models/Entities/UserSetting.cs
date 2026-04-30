using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("UserSettings")]
public class UserSetting
{
    [Key]
    [MaxLength(256)]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
