using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VirtmaAi.Models.Entities;

public enum ConversationMode
{
    Chat = 0,
    Code = 1,
    CoWork = 2
}

[Table("Conversations")]
public class Conversation
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(256)]
    public string Title { get; set; } = "New conversation";

    public ConversationMode Mode { get; set; } = ConversationMode.Chat;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Guid? ModelId { get; set; }

    public int ContextLimit { get; set; }

    [MaxLength(1024)]
    public string? ProjectDir { get; set; }

    public bool ExternalContextAllowed { get; set; }

    public ICollection<Message> Messages { get; set; } = new List<Message>();
}
