using CommunityToolkit.Mvvm.ComponentModel;
using VirtmaAi.Models.Entities;

namespace VirtmaAi.ViewModels.Chat;

public sealed partial class ConversationListItem : ObservableObject
{
    public Guid Id { get; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private ConversationMode _mode;

    [ObservableProperty]
    private DateTime _updatedAt;

    public ConversationListItem(Conversation c)
    {
        Id = c.Id;
        Title = c.Title;
        Mode = c.Mode;
        UpdatedAt = c.UpdatedAt;
    }
}
