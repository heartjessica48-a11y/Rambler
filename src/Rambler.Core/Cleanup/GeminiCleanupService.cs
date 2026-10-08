using System.Text;
using System.Text.Json;
using Rambler.Core.Gemini;

namespace Rambler.Core.Cleanup;

public interface ICleanupService
{
    Task<string> CleanAsync(string transcript, CleanupOptions options, string apiKey, CancellationToken ct);
}

/// <summary>Prompt Cleanup via a Gemini Flash-family model (generateContent, non-streaming).</summary>
public sealed class GeminiCleanupService(GeminiHttp http) : ICleanupService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    public async Task<string> CleanAsync(string transcript, CleanupOptions options, string apiKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        var uri = GeminiEndpoints.GenerateContent(options.Model);

        JsonDocument doc;
        try
        {
            doc = await http.PostJsonAsync(uri, apiKey, CleanupPromptBuilder.BuildRequestJson(transcript, options), RequestTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (GeminiException ex) when (ex.Kind == GeminiErrorKind.BadRequest &&
                                         !string.IsNullOrWhiteSpace(options.ThinkingLevel) &&
                                         ex.Message.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            // Not every Flash model accepts every thinking level (e.g. MINIMAL); retry with the model default.
            doc = await http.PostJsonAsync(uri, apiKey, CleanupPromptBuilder.BuildRequestJson(transcript, options, includeThinking: false),
                RequestTimeout, ct).ConfigureAwait(false);
        }

        using (doc)
        {
            var text = ExtractText(doc.RootElement);
            text = CleanupOutputSanitizer.Sanitize(text);
            if (string.IsNullOrWhiteSpace(text))
                throw new GeminiException(GeminiErrorKind.EmptyResult, "The cleanup model returned no text.");
            return text;
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
