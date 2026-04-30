namespace VirtmaAi.Services.AI.Providers;

/// <summary>
/// Streaming parser that splits inline <c>&lt;think&gt;...&lt;/think&gt;</c> blocks out of a
/// content stream and routes them as separate <see cref="ThinkingChunk"/> events. Models like
/// qwen3 emit reasoning inside these tags as part of normal content; if we don't separate it the
/// tags leak into the chat body and (worse) confuse the markdown renderer enough that surrounding
/// text disappears.
///
/// The parser handles partial tags that span chunk boundaries (we only emit text we're certain
/// belongs to one side or the other; ambiguous tail bytes are buffered and re-evaluated on the
/// next push).
/// </summary>
public sealed class ThinkTagSplitter
{
    private const string OpenTag = "<think>";
    private const string CloseTag = "</think>";

    private readonly global::System.Text.StringBuilder _pending = new();
    private bool _insideThinking;

    /// <summary>
    /// Push a streamed text chunk. Yields zero or more events, alternating between content and
    /// thinking as the parser crosses tag boundaries.
    /// </summary>
    public IEnumerable<ChatEvent> Push(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) yield break;
        _pending.Append(chunk);

        while (_pending.Length > 0)
        {
            var buf = _pending.ToString();
            var seekTag = _insideThinking ? CloseTag : OpenTag;
            int idx = buf.IndexOf(seekTag, StringComparison.OrdinalIgnoreCase);

            if (idx >= 0)
            {
                if (idx > 0)
                {
                    var emitted = buf.Substring(0, idx);
                    yield return _insideThinking ? new ThinkingChunk(emitted) : (ChatEvent)new ContentChunk(emitted);
                }
                _pending.Remove(0, idx + seekTag.Length);
                _insideThinking = !_insideThinking;
                continue;
            }

            // Tag wasn't found in full. Hold back the trailing portion that *could* be the start
            // of a partial tag so we don't accidentally emit "<thi" as content. Worst case we
            // hold up to (tag length - 1) characters until the next chunk arrives.
            var safeLen = SafeFlushLength(buf, seekTag);
            if (safeLen > 0)
            {
                var emitted = buf.Substring(0, safeLen);
                yield return _insideThinking ? new ThinkingChunk(emitted) : (ChatEvent)new ContentChunk(emitted);
                _pending.Remove(0, safeLen);
            }
            yield break;
        }
    }

    /// <summary>
    /// Flush whatever's left in the buffer at end-of-stream. If we ended mid-tag (model truncated),
    /// the remainder is emitted with the current side so nothing is silently dropped.
    /// </summary>
    public IEnumerable<ChatEvent> Flush()
    {
        if (_pending.Length == 0) yield break;
        var emitted = _pending.ToString();
        _pending.Clear();
        yield return _insideThinking ? new ThinkingChunk(emitted) : (ChatEvent)new ContentChunk(emitted);
    }

    /// <summary>
    /// Returns the largest prefix of <paramref name="buf"/> we can safely emit without risking
    /// a split tag. If the tail of the buffer matches a prefix of <paramref name="tag"/>, we hold
    /// it back; otherwise we emit the whole buffer.
    /// </summary>
    private static int SafeFlushLength(string buf, string tag)
    {
        int max = Math.Min(buf.Length, tag.Length - 1);
        for (int hold = max; hold > 0; hold--)
        {
            if (string.CompareOrdinal(buf, buf.Length - hold, tag, 0, hold) == 0)
                return buf.Length - hold;
        }
        return buf.Length;
    }
}
