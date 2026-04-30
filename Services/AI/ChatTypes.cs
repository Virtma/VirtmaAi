namespace VirtmaAi.Services.AI;

public sealed record ChatRequest(
    string ModelId,
    IReadOnlyList<ChatMessage> Messages,
    string? SystemPrompt = null,
    float Temperature = 0.7f,
    int? MaxTokens = null,
    IReadOnlyList<string>? StopSequences = null);

/// <summary>
/// A single base64-encoded image to send alongside a message.
/// Used for vision requests (jpg/jpeg/png/gif/webp).
/// </summary>
public sealed record MessageImage(string Base64Data, string MimeType);

/// <summary>
/// A single turn in the conversation.
/// <para>
/// <see cref="Images"/> carries vision attachments for the current user turn only.
/// They are never persisted to the DB or re-sent in follow-up iterations.
/// </para>
/// </summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<MessageImage>? Images = null);

public enum ChatRole
{
    System,
    User,
    Assistant,
    Thinking
}

public abstract record ChatEvent;
public sealed record ContentChunk(string Text) : ChatEvent;
public sealed record ThinkingChunk(string Text) : ChatEvent;
public sealed record ToolCallRequested(string Name, string ArgumentsJson) : ChatEvent;
public sealed record StreamCompleted(int? PromptTokens, int? CompletionTokens, string? StopReason) : ChatEvent;
public sealed record StreamError(string Message, Exception? Exception) : ChatEvent;
