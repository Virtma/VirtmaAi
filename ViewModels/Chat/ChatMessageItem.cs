using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Media;

namespace VirtmaAi.ViewModels.Chat;

public sealed partial class ChatMessageItem : ObservableObject
{
    private static readonly InlineMediaDetector Detector = new();

    public Guid Id { get; }
    public MessageRole Role { get; }

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _thinking = string.Empty;

    /// <summary>True while the live "thinking" stream is producing tokens.</summary>
    [ObservableProperty]
    private bool _isThinkingActive;

    /// <summary>
    /// While the model is actively thinking we render the buffer expanded and live-updating.
    /// Once thinking stops, we auto-collapse so the user can review or hide it. Defaults to
    /// expanded so the live stream is visible.
    /// </summary>
    [ObservableProperty]
    private bool _isThinkingExpanded = true;

    [ObservableProperty]
    private bool _isStreaming;

    public DateTime CreatedAt { get; }

    public bool IsUser      => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;

    /// <summary>True once any thinking text has been buffered or stored.</summary>
    public bool HasThinking => !string.IsNullOrEmpty(Thinking);

    /// <summary>
    /// True when the message should be rendered by <see cref="HighlightedMarkdownView"/>
    /// (WebView + highlight.js) rather than the native streaming renderer.
    /// This is true for every settled (non-streaming) message so that:
    /// <list type="bullet">
    ///   <item>Inline code (<c>`…`</c>), fenced code blocks, and all other markdown
    ///         are rendered faithfully by Markdig + highlight.js.</item>
    ///   <item>Content is always text-selectable (WebView allows native selection).</item>
    ///   <item>The final content is reliably shown — the WebView is activated exactly
    ///         once, when streaming ends, preventing the stale-invisible-navigation race.</item>
    /// </list>
    /// </summary>
    public bool UseWebView => !IsStreaming;

    /// <summary>
    /// Preserved for compatibility — callers that still reference HasCodeBlocks will
    /// always get <c>true</c> once streaming ends, matching the old behaviour for
    /// code-containing messages while also covering inline-code and plain text.
    /// </summary>
    public bool HasCodeBlocks => Content.Contains('`', StringComparison.Ordinal);

    /// <summary>"▾ Thinking" while expanded, "▸ Thinking" while collapsed — bound by the toggle button.</summary>
    public string ThinkingHeader => IsThinkingActive
        ? "● Thinking…"
        : (IsThinkingExpanded ? "▾ Thinking" : "▸ Thinking");

    public IRelayCommand ToggleThinkingCommand { get; }

    public ObservableCollection<InlineImage> Images { get; } = new();
    public ObservableCollection<ChartDefinition> Charts { get; } = new();
    public bool HasMedia => Images.Count > 0 || Charts.Count > 0;

    public ChatMessageItem(Message m)
    {
        Id = m.Id;
        Role = m.Role;
        Content = m.Content;
        CreatedAt = m.CreatedAt;
        ToggleThinkingCommand = new RelayCommand(() => IsThinkingExpanded = !IsThinkingExpanded);
        // Persisted (non-streamed) messages: collapse the thinking by default.
        IsThinkingExpanded = false;
    }

    public ChatMessageItem(Guid id, MessageRole role, string content = "")
    {
        Id = id;
        Role = role;
        Content = content;
        CreatedAt = DateTime.UtcNow;
        ToggleThinkingCommand = new RelayCommand(() => IsThinkingExpanded = !IsThinkingExpanded);
    }

    partial void OnThinkingChanged(string value)
    {
        OnPropertyChanged(nameof(HasThinking));
        OnPropertyChanged(nameof(ThinkingHeader));
    }

    partial void OnIsThinkingActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(ThinkingHeader));
        // Auto-collapse once thinking stream completes so the body of the response is the focus.
        if (!value) IsThinkingExpanded = false;
    }

    partial void OnIsThinkingExpandedChanged(bool value) => OnPropertyChanged(nameof(ThinkingHeader));

    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(HasCodeBlocks));
        if (IsStreaming) return;
        RefreshMedia(value);
    }

    partial void OnIsStreamingChanged(bool value)
    {
        if (!value)
        {
            OnPropertyChanged(nameof(UseWebView));
            OnPropertyChanged(nameof(HasCodeBlocks));
            RefreshMedia(Content);
        }
    }

    private void RefreshMedia(string text)
    {
        var detected = Detector.Detect(text);
        Images.Clear();
        Charts.Clear();
        foreach (var m in detected)
        {
            if (m is InlineImage img) Images.Add(img);
            else if (m is InlineChart ch) Charts.Add(ch.Chart);
        }
        OnPropertyChanged(nameof(HasMedia));
    }
}
