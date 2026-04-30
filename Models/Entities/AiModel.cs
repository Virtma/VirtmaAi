using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("AiModels")]
public class AiModel
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Provider { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Endpoint { get; set; }

    public string? Parameters { get; set; }

    public long SizeBytes { get; set; }

    public string? HardwareReqs { get; set; }

    public bool IsLocal { get; set; }

    [MaxLength(1024)]
    public string? DownloadedPath { get; set; }

    /// <summary>
    /// Optional per-model public/published API key (e.g. for an authenticated proxy in front of
    /// the endpoint). Sent as <c>X-API-Key</c> when calling this model's endpoint.
    /// </summary>
    [MaxLength(512)]
    public string? PublicApiKey { get; set; }

    /// <summary>
    /// Optional per-model private/secret API key. Sent as the bearer token in the Authorization
    /// header when calling this model's endpoint. Stored as-is in the DB; for higher-sensitivity
    /// keys use the API Keys page (encrypted SecureStorage) instead.
    /// </summary>
    [MaxLength(512)]
    public string? PrivateApiKey { get; set; }
}
