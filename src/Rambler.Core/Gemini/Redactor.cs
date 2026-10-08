using System.Text.RegularExpressions;

namespace Rambler.Core.Gemini;

/// <summary>Scrubs API keys from any text that might be shown or logged.</summary>
public static partial class Redactor
{
    private static string? s_secret;

    /// <summary>Registers the active key so exact occurrences are also removed.</summary>
    public static void RegisterSecret(string? secret) =>
        s_secret = string.IsNullOrWhiteSpace(secret) ? null : secret;

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var secret = s_secret;
        if (secret is { Length: >= 8 })
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        text = KeyParam().Replace(text, "$1[REDACTED]");
        text = KeyHeader().Replace(text, "$1[REDACTED]");
        return GoogleKey().Replace(text, "[REDACTED]");
    }

    [GeneratedRegex(@"([?&]key=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyParam();

    [GeneratedRegex(@"(x-goog-api-key[""']?\s*[:=]\s*[""']?)[^\s""',]+", RegexOptions.IgnoreCase)]
    private static partial Regex KeyHeader();

    // Google API keys: "AIza" followed by 35 URL-safe characters.
    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{35}")]
    private static partial Regex GoogleKey();
}
