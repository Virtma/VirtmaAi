using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

[Table("MessageAttachments")]
public class MessageAttachment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MessageId { get; set; }

    [ForeignKey(nameof(MessageId))]
    public Message? Message { get; set; }

    [MaxLength(1024)]
    public string FilePath { get; set; } = string.Empty;

    [MaxLength(128)]
    public string Mime { get; set; } = string.Empty;

    public long Bytes { get; set; }
}
