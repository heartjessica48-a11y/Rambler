using System.Text;
using System.Text.Json;
using Rambler.Core.Gemini;

namespace Rambler.Core.Cleanup;

public interface ICleanupService
{
    /// <summary>
    /// Cleans a transcript (whole dictation) or one progressive chunk. Progressive requests may return an
    /// empty string when the chunk has no new content; whole-dictation requests never return empty.
    /// </summary>
    Task<string> CleanAsync(CleanupRequest request, CleanupOptions options, string apiKey, CancellationToken ct);
}

/// <summary>Prompt Cleanup via a Gemini Flash-family model (generateContent, non-streaming).</summary>
public sealed class GeminiCleanupService(GeminiHttp http) : ICleanupService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    public async Task<string> CleanAsync(CleanupRequest request, CleanupOptions options, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Transcript);
        var uri = GeminiEndpoints.GenerateContent(options.Model);

        JsonDocument doc;
        try
        {
            doc = await http.PostJsonAsync(uri, apiKey, CleanupPromptBuilder.BuildRequestJson(request, options), RequestTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (GeminiException ex) when (ex.Kind == GeminiErrorKind.BadRequest &&
                                         !string.IsNullOrWhiteSpace(options.ThinkingLevel) &&
                                         ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            // Not every Flash model accepts every thinking level (e.g. MINIMAL); retry with the model default.
            doc = await http.PostJsonAsync(uri, apiKey, CleanupPromptBuilder.BuildRequestJson(request, options, includeThinking: false),
                RequestTimeout, ct).ConfigureAwait(false);
        }

        using (doc)
        {
            var raw = ExtractText(doc.RootElement);
            if (!request.Progressive)
            {
                var text = CleanupOutputSanitizer.Sanitize(raw);
                if (string.IsNullOrWhiteSpace(text))
                    throw new GeminiException(GeminiErrorKind.EmptyResult, "The cleanup model returned no text.");
                return text;
            }
            return CleanupOutputSanitizer.SanitizeChunk(raw, request.CommittedContext);
        }
    }

    /// <summary>Concatenates non-thought text parts of the first candidate; throws on blocks.</summary>
    internal static string ExtractText(JsonElement root)
    {
        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var block))
        {
            throw new GeminiException(GeminiErrorKind.Blocked, $"Cleanup request blocked ({block.GetString()}).");
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            throw new GeminiException(GeminiErrorKind.EmptyResult, "The cleanup model returned no candidates.");
        }

        var candidate = candidates[0];
        var sb = new StringBuilder();
        if (candidate.TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
            }
        }

        if (sb.Length == 0 && candidate.TryGetProperty("finishReason", out var reason))
        {
            var r = reason.GetString();
            if (r is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or "RECITATION")
                throw new GeminiException(GeminiErrorKind.Blocked, $"Cleanup response blocked ({r}).");
        }

        return sb.ToString();
    }
}

/// <summary>Removes wrappers a model sometimes adds despite the output contract.</summary>
public static class CleanupOutputSanitizer
{
    /// <summary>
    /// Sanitizes one progressive chunk: like <see cref="Sanitize"/>, but keeps up to two leading line breaks
    /// (the chunk starts a new line/paragraph) and drops text that merely repeats the committed context.
    /// </summary>
    public static string SanitizeChunk(string? text, string? committedContext)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Replace("\r\n", "\n");

        // An echoed context block is never new output.
        var ctxEnd = s.IndexOf(CleanupPromptBuilder.ContextCloseTag, StringComparison.OrdinalIgnoreCase);
        if (ctxEnd >= 0) s = s[(ctxEnd + CleanupPromptBuilder.ContextCloseTag.Length)..];
        var openIdx = s.IndexOf("<transcript", StringComparison.OrdinalIgnoreCase);
        if (openIdx >= 0)
        {
            var close = s.IndexOf('>', openIdx);
            // The echoed tag's own line break isn't formatting.
            if (close > 0) s = s[..openIdx] + s[(close + 1)..].TrimStart('\r', '\n');
        }
        var closeIdx = s.IndexOf(CleanupPromptBuilder.TranscriptCloseTag, StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0) s = s[..closeIdx];

        var trimmedStart = s.TrimStart(' ', '\t');
        var leadingBreaks = 0;
        while (leadingBreaks < trimmedStart.Length && trimmedStart[leadingBreaks] is '\n' or '\r') leadingBreaks++;
        var body = Sanitize(trimmedStart);
        if (body.Length == 0) return string.Empty;

        body = StripRepeatedContext(body, committedContext);
        if (body.Length == 0) return string.Empty;
        return new string('\n', Math.Min(leadingBreaks, 2)) + body;
    }

    /// <summary>Removes a leading copy of the committed context's last sentence, if the model repeated it.</summary>
    internal static string StripRepeatedContext(string output, string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return output;
        var ctx = context.TrimEnd();
        var cut = ctx.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, ctx.Length - 2));
        var lastSentence = (cut >= 0 ? ctx[(cut + 1)..] : ctx).Trim();
        if (lastSentence.Length < 12) return output;
        if (output.StartsWith(ctx.Trim(), StringComparison.Ordinal)) return output[ctx.Trim().Length..].TrimStart();
        if (output.StartsWith(lastSentence, StringComparison.Ordinal)) return output[lastSentence.Length..].TrimStart();
        return output;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = text.Trim();

        // Echoed transcript tags.
        if (s.StartsWith(CleanupPromptBuilder.TranscriptOpenTag, StringComparison.OrdinalIgnoreCase))
            s = s[CleanupPromptBuilder.TranscriptOpenTag.Length..];
        if (s.EndsWith(CleanupPromptBuilder.TranscriptCloseTag, StringComparison.OrdinalIgnoreCase))
            s = s[..^CleanupPromptBuilder.TranscriptCloseTag.Length];
        s = s.Trim();

        // A single markdown fence around the whole output.
        if (s.StartsWith("```", StringComparison.Ordinal) && s.EndsWith("```", StringComparison.Ordinal) && s.Length >= 6)
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline > 0 && firstNewline < s.Length - 3)
            {
                var inner = s[(firstNewline + 1)..^3];
                if (!inner.Contains("```", StringComparison.Ordinal)) s = inner.Trim();
            }
        }

        // Quotation marks wrapping the entire result (only when no other quotes of that kind are inside).
        foreach (var (open, close) in new[] { ('"', '"'), ('“', '”') })
        {
            if (s.Length >= 2 && s[0] == open && s[^1] == close)
            {
                var inner = s[1..^1];
                if (!inner.Contains(open) && !inner.Contains(close)) s = inner.Trim();
            }
        }

        return s;
    }
}
