using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtmaAi.Models.Entities;
using VirtmaAi.Services.AI;
using VirtmaAi.Services.Data;
using VirtmaAi.Services.Plugins;
using VirtmaAi.Services.Routines;
using VirtmaAi.Services.Skills;

namespace VirtmaAi.Services.ExternalApi;

public sealed class ExternalApiHost : IExternalApiHost
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IExternalApiKeyService _keys;
    private readonly IProviderRouter _router;
    private readonly IDatabaseService _db;
    private readonly IPluginHost _plugins;
    private readonly ISkillRegistry _skills;
    private readonly IRoutineScheduler _routines;
    private readonly ILogger<ExternalApiHost> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ExternalApiHost(
        IExternalApiKeyService keys,
        IProviderRouter router,
        IDatabaseService db,
        IPluginHost plugins,
        ISkillRegistry skills,
        IRoutineScheduler routines,
        ILogger<ExternalApiHost> logger)
    {
        _keys = keys;
        _router = router;
        _db = db;
        _plugins = plugins;
        _skills = skills;
        _routines = routines;
        _logger = logger;
    }

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }

    public Task StartAsync(int port, CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _logger.LogInformation("External API listening on http://127.0.0.1:{Port}/", port);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _listener = null;
        _loop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "http://localhost";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            if (method == "GET" && path == "/v1/ping")
            {
                await WriteJsonAsync(ctx, 200, new { status = "ok" }).ConfigureAwait(false);
                return;
            }

            if (!await AuthorizeAsync(ctx).ConfigureAwait(false)) return;

            if (method == "POST" && path == "/v1/chat") { await HandleChatAsync(ctx, ct).ConfigureAwait(false); return; }
            if (method == "POST" && path == "/v1/conversations") { await HandleCreateConversationAsync(ctx).ConfigureAwait(false); return; }
            if (method == "GET" && path.StartsWith("/v1/conversations/", StringComparison.Ordinal)) { await HandleGetConversationAsync(ctx, path).ConfigureAwait(false); return; }
            if (method == "POST" && path.StartsWith("/v1/plugins/", StringComparison.Ordinal) && path.EndsWith("/invoke", StringComparison.Ordinal)) { await HandlePluginInvokeAsync(ctx, path, ct).ConfigureAwait(false); return; }
            if (method == "POST" && path.StartsWith("/v1/skills/", StringComparison.Ordinal) && path.EndsWith("/invoke", StringComparison.Ordinal)) { await HandleSkillInvokeAsync(ctx, path, ct).ConfigureAwait(false); return; }
            if (method == "POST" && path == "/v1/routines") { await HandleCreateRoutineAsync(ctx).ConfigureAwait(false); return; }

            await WriteJsonAsync(ctx, 404, new { error = "not found", path }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "External API handler error");
            try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<bool> AuthorizeAsync(HttpListenerContext ctx)
    {
        var auth = ctx.Request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, 401, new { error = "missing bearer token" }).ConfigureAwait(false);
            return false;
        }
        var token = auth.Substring("Bearer ".Length).Trim();
        var key = await _keys.VerifyAsync(token).ConfigureAwait(false);
        if (key is null)
        {
            await WriteJsonAsync(ctx, 401, new { error = "invalid token" }).ConfigureAwait(false);
            return false;
        }
        return true;
    }

    private async Task HandleChatAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadJsonAsync<ChatRequestDto>(ctx).ConfigureAwait(false);
        if (body is null) { await WriteJsonAsync(ctx, 400, new { error = "invalid body" }).ConfigureAwait(false); return; }

        var provider = _router.Get(body.Provider ?? "ollama");
        var messages = (body.Messages ?? Array.Empty<ChatMessageDto>())
            .Select(m => new ChatMessage(ParseRole(m.Role), m.Content ?? string.Empty))
            .ToList();
        var request = new ChatRequest(body.Model ?? string.Empty, messages, body.SystemPrompt);

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;
        using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        await foreach (var evt in provider.StreamAsync(request, ct).ConfigureAwait(false))
        {
            var payload = evt switch
            {
                ContentChunk c => JsonSerializer.Serialize(new { type = "content", text = c.Text }, JsonOpts),
                ThinkingChunk t => JsonSerializer.Serialize(new { type = "thinking", text = t.Text }, JsonOpts),
                StreamCompleted d => JsonSerializer.Serialize(new { type = "done", promptTokens = d.PromptTokens, completionTokens = d.CompletionTokens, stopReason = d.StopReason }, JsonOpts),
                StreamError e => JsonSerializer.Serialize(new { type = "error", message = e.Message }, JsonOpts),
                _ => null
            };
            if (payload is null) continue;
            await writer.WriteAsync("data: ").ConfigureAwait(false);
            await writer.WriteAsync(payload).ConfigureAwait(false);
            await writer.WriteAsync("\n\n").ConfigureAwait(false);
        }
        try { ctx.Response.OutputStream.Close(); } catch { }
    }

    private async Task HandleCreateConversationAsync(HttpListenerContext ctx)
    {
        var body = await ReadJsonAsync<CreateConversationDto>(ctx).ConfigureAwait(false) ?? new CreateConversationDto();
        await using var db = _db.CreateContext();
        var conv = new Conversation
        {
            Title = string.IsNullOrWhiteSpace(body.Title) ? "External chat" : body.Title!,
            Mode = Enum.TryParse<ConversationMode>(body.Mode, true, out var m) ? m : ConversationMode.Chat
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await WriteJsonAsync(ctx, 201, new { id = conv.Id, title = conv.Title, mode = conv.Mode.ToString() }).ConfigureAwait(false);
    }

    private async Task HandleGetConversationAsync(HttpListenerContext ctx, string path)
    {
        var idStr = path.Substring("/v1/conversations/".Length);
        if (!Guid.TryParse(idStr, out var id)) { await WriteJsonAsync(ctx, 400, new { error = "bad id" }).ConfigureAwait(false); return; }
        await using var db = _db.CreateContext();
        var conv = await db.Conversations.FindAsync(id).ConfigureAwait(false);
        if (conv is null) { await WriteJsonAsync(ctx, 404, new { error = "not found" }).ConfigureAwait(false); return; }
        var messages = await db.Messages.Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt).ToListAsync().ConfigureAwait(false);
        await WriteJsonAsync(ctx, 200, new
        {
            id = conv.Id,
            title = conv.Title,
            mode = conv.Mode.ToString(),
            messages = messages.Select(m => new { id = m.Id, role = m.Role.ToString(), content = m.Content, createdAt = m.CreatedAt })
        }).ConfigureAwait(false);
    }

    private async Task HandlePluginInvokeAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        var name = Uri.UnescapeDataString(path.Substring("/v1/plugins/".Length, path.Length - "/v1/plugins/".Length - "/invoke".Length));
        var body = await ReadJsonAsync<InvokeDto>(ctx).ConfigureAwait(false) ?? new InvokeDto();
        PluginInvocationResult result;
        if (Guid.TryParse(name, out var id)) result = await _plugins.InvokeAsync(id, body.Input ?? string.Empty, ct).ConfigureAwait(false);
        else result = await _plugins.InvokeBuiltInAsync(name, body.Input ?? string.Empty, ct).ConfigureAwait(false);
        await WriteJsonAsync(ctx, result.Success ? 200 : 500, new { success = result.Success, output = result.Output, error = result.Error }).ConfigureAwait(false);
    }

    private async Task HandleSkillInvokeAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        var name = Uri.UnescapeDataString(path.Substring("/v1/skills/".Length, path.Length - "/v1/skills/".Length - "/invoke".Length));
        var skills = await _skills.ListAsync().ConfigureAwait(false);
        Skill? match = Guid.TryParse(name, out var id)
            ? skills.FirstOrDefault(s => s.Id == id)
            : skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) { await WriteJsonAsync(ctx, 404, new { error = "skill not found" }).ConfigureAwait(false); return; }
        await WriteJsonAsync(ctx, 200, new { id = match.Id, name = match.Name, instructions = match.InstructionsMd }).ConfigureAwait(false);
    }

    private async Task HandleCreateRoutineAsync(HttpListenerContext ctx)
    {
        var body = await ReadJsonAsync<CreateRoutineDto>(ctx).ConfigureAwait(false);
        if (body is null) { await WriteJsonAsync(ctx, 400, new { error = "invalid body" }).ConfigureAwait(false); return; }
        Guid? modelId = Guid.TryParse(body.Model, out var mid) ? mid : null;
        var routine = new Routine
        {
            Name = body.Name ?? "External routine",
            CronExpression = body.Cron ?? "*/5 * * * *",
            Instructions = body.Instructions ?? string.Empty,
            ModelId = modelId,
            ResponseHandling = Enum.TryParse<RoutineResponseHandling>(body.Handling, true, out var h) ? h : RoutineResponseHandling.Log,
            Enabled = body.Enabled ?? true
        };
        var created = await _routines.CreateAsync(routine).ConfigureAwait(false);
        await WriteJsonAsync(ctx, 201, new { id = created.Id, name = created.Name, next = created.NextRunAt }).ConfigureAwait(false);
    }

    private static ChatRole ParseRole(string? s) => s?.ToLowerInvariant() switch
    {
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "thinking" => ChatRole.Thinking,
        _ => ChatRole.User
    };

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return default;
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch { return default; }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    private sealed class ChatRequestDto
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? SystemPrompt { get; set; }
        public ChatMessageDto[]? Messages { get; set; }
    }

    private sealed class ChatMessageDto
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
    }

    private sealed class CreateConversationDto
    {
        public string? Title { get; set; }
        public string? Mode { get; set; }
    }

    private sealed class InvokeDto
    {
        public string? Input { get; set; }
    }

    private sealed class CreateRoutineDto
    {
        public string? Name { get; set; }
        public string? Cron { get; set; }
        public string? Instructions { get; set; }
        public string? Model { get; set; }
        public string? Handling { get; set; }
        public bool? Enabled { get; set; }
    }
}
