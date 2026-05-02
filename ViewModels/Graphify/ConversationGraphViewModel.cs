using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Notifications;

namespace VirtmaAi.ViewModels.Graphify;

/// <summary>
/// Builds an Obsidian-style force-directed graph of a single conversation: messages as nodes,
/// causality + topical similarity as edges. The page hosts a vis-network WebView and feeds it the
/// JSON produced here.
/// </summary>
public sealed partial class ConversationGraphViewModel : ViewModelBase
{
    private readonly IDatabaseService _db;
    private readonly IToastService _toast;
    private readonly ILogger<ConversationGraphViewModel> _logger;

    public ConversationGraphViewModel(
        IDatabaseService db,
        IToastService toast,
        ILogger<ConversationGraphViewModel> logger)
    {
        _db = db;
        _toast = toast;
        _logger = logger;
    }

    public ObservableCollection<ConversationGraphItem> Conversations { get; } = new();

    [ObservableProperty]
    private ConversationGraphItem? _selected;

    [ObservableProperty]
    private string? _graphJson;

    [ObservableProperty]
    private string _summary = "Loading conversations…";

    /// <summary>Raised whenever a freshly built graph payload is ready for the WebView to render.</summary>
    public event EventHandler<string>? GraphReady;
    /// <summary>Raised when the page should clear its current graph (e.g. nothing selected).</summary>
    public event EventHandler? GraphCleared;

    partial void OnSelectedChanged(ConversationGraphItem? value)
    {
        if (value is null)
        {
            GraphJson = null;
            GraphCleared?.Invoke(this, EventArgs.Empty);
            return;
        }
        _ = BuildAsync(value.Id);
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_db.Current is null) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var convs = await ctx.Conversations
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new { c.Id, c.Title, c.Mode, c.UpdatedAt })
                .ToListAsync();
            Conversations.Clear();
            foreach (var c in convs)
                Conversations.Add(new ConversationGraphItem(c.Id, c.Title, c.Mode.ToString(), c.UpdatedAt));
            Summary = Conversations.Count == 0
                ? "No conversations yet — start one in the Chat tab."
                : $"{Conversations.Count} conversation(s).";
            // Auto-pick the most-recent conversation so the user sees something immediately.
            if (Selected is null && Conversations.Count > 0)
                Selected = Conversations[0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "load conversations for graph");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not load conversations: " + ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync() => await LoadAsync();

    private async Task BuildAsync(Guid conversationId)
    {
        try
        {
            IsBusy = true;
            await using var ctx = _db.CreateContext();
            var conv = await ctx.Conversations.FindAsync(conversationId);
            var msgs = await ctx.Messages
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .ToListAsync();

            var payload = BuildGraphPayload(conv, msgs);
            GraphJson = payload;
            GraphReady?.Invoke(this, payload);
            Summary = $"{msgs.Count} message(s) · {conv?.Title ?? "(untitled)"}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "build conversation graph");
            ErrorMessage = ex.Message;
            await _toast.ErrorAsync("Could not build graph: " + ex.Message);
        }
        finally { IsBusy = false; }
    }

    private static string BuildGraphPayload(Conversation? conv, IReadOnlyList<Message> msgs)
    {
        // ── Re-order so each thinking node appears BEFORE its parent assistant node ────────
        //
        // In the DB, PersistAssistantAsync saves the assistant row first (earlier CreatedAt),
        // then the thinking row — so raw DB order puts thinking after assistant, which is
        // the wrong flow direction for the graph (thinking logically PRECEDES the response).
        //
        // Build a lookup of thinkingMsg → parentAssistantId and reconstruct the list with
        // each thinking node inserted just before its assistant.
        var thinkingParentMap = msgs
            .Where(m => m.Role == MessageRole.Thinking && m.ParentMessageId.HasValue)
            .ToDictionary(m => m.ParentMessageId!.Value);

        var ordered = new List<Message>(msgs.Count);
        foreach (var m in msgs)
        {
            if (m.Role == MessageRole.Thinking) continue; // injected just before its parent below
            if (m.Role == MessageRole.Assistant && thinkingParentMap.TryGetValue(m.Id, out var thinkingMsg))
                ordered.Add(thinkingMsg); // thinking node first
            ordered.Add(m);
        }

        var nodes = new List<object>(ordered.Count);
        var edges = new List<object>();

        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            var role = ClassifyRole(m);
            var label = Snippet(m.Content, 56);
            var weight = 10 + Math.Min(40, (m.Content?.Length ?? 0) / 80);
            nodes.Add(new
            {
                id = m.Id.ToString("N"),
                role,
                label,
                tooltip = Snippet(m.Content, 360),
                weight,
                createdAt = m.CreatedAt
            });

            // Sequential edge to the previous message (the temporal causality chain).
            if (i > 0)
            {
                edges.Add(new
                {
                    from = ordered[i - 1].Id.ToString("N"),
                    to = m.Id.ToString("N"),
                    implicit_ = false
                });
            }

            // Explicit parent-child edge for non-thinking parent-child relationships.
            // Thinking nodes are already placed sequentially before their parent assistant
            // in `ordered`, so the sequential edge above already captures that relationship.
            // Adding a second explicit edge in the old direction (assistant → thinking) would
            // create a cycle in the graph; skipping it here is the correct behaviour.
            if (m.ParentMessageId is Guid parent &&
                m.Role != MessageRole.Thinking &&
                ordered.Any(x => x.Id == parent))
            {
                edges.Add(new
                {
                    from = parent.ToString("N"),
                    to = m.Id.ToString("N"),
                    implicit_ = false
                });
            }
        }

        // Topical edges: messages sharing rare-ish keywords get a dashed implicit edge. Cheap O(n²)
        // scan — fine for typical conversation lengths.
        var tokenSets = ordered.Select(m => Tokenize(m.Content)).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            for (int j = i + 2; j < ordered.Count; j++) // skip immediate neighbor (already linked)
            {
                var shared = tokenSets[i].Intersect(tokenSets[j]).Take(3).Count();
                if (shared >= 2)
                {
                    edges.Add(new
                    {
                        from = ordered[i].Id.ToString("N"),
                        to = ordered[j].Id.ToString("N"),
                        implicit_ = true,
                        arrow = false
                    });
                }
            }
        }

        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };
        // Map `implicit_` to `implicit` because `implicit` is a C# keyword.
        var transformedEdges = edges.Select(e =>
        {
            var dict = JsonSerializer.SerializeToElement(e, options);
            var clone = new Dictionary<string, object?>();
            foreach (var prop in dict.EnumerateObject())
                clone[prop.Name == "implicit_" ? "implicit" : prop.Name] = JsonElementToObject(prop.Value);
            return clone;
        }).ToList();

        var payload = new
        {
            title = conv?.Title ?? "(untitled)",
            mode = conv?.Mode.ToString(),
            nodes,
            edges = transformedEdges
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };

    private static string ClassifyRole(Message m)
    {
        if (m.Role == MessageRole.Thinking) return "thinking";
        if (m.Role == MessageRole.Assistant) return "assistant";
        if (m.Role == MessageRole.System) return "tool";
        // Tool-result user messages produced by the chat loop have a recognizable prefix.
        if (m.Role == MessageRole.User &&
            m.Content.StartsWith("[tool execution results", StringComparison.OrdinalIgnoreCase))
            return "tool";
        return "user";
    }

    private static string Snippet(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "…";
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","of","to","and","or","in","on","for","with","is","are","was","were","be","been","being",
        "this","that","it","its","as","at","by","from","but","if","so","than","then","not","no","yes",
        "i","you","we","they","he","she","me","my","your","our","their","his","her",
        "do","does","did","done","have","has","had","can","could","would","should","will","may","might",
        "what","when","where","why","how","which","who","whom","whose"
    };

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[a-z][a-z0-9_-]{3,}")
            .Select(m => m.Value)
            .Where(w => !Stopwords.Contains(w))
            .Distinct()
            .Take(40)
            .ToList();
        return words;
    }
}

public sealed record ConversationGraphItem(Guid Id, string Title, string Mode, DateTime UpdatedAt)
{
    public string Display => $"{Title} · {Mode} · {UpdatedAt:yyyy-MM-dd HH:mm}";
}
