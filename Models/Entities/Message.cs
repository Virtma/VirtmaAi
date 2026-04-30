using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

public enum MessageRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Thinking = 3
}

[Table("Messages")]
public class Message
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    [ForeignKey(nameof(ConversationId))]
    public Conversation? Conversation { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = string.Empty;

    public int TokenCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid? ModelId { get; set; }

    public Guid? ParentMessageId { get; set; }

    public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
}
