using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

public enum ApiKeyCategory
{
    Dev = 0,
    Prod = 1
}

[Table("ApiKeys")]
public class ApiKey
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(128)]
    public string ServiceName { get; set; } = string.Empty;

    [MaxLength(128)]
    public string KeyName { get; set; } = string.Empty;

    // Value is stored via SecureStorage — this field is the SecureStorage lookup key.
    [MaxLength(256)]
    public string SecureStorageKey { get; set; } = string.Empty;

    public ApiKeyCategory Category { get; set; } = ApiKeyCategory.Dev;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
