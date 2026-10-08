using System.Text.RegularExpressions;

namespace Rambler.Core.Dictation;

/// <summary>
/// Minimal, deterministic formatting for Smart Only: only explicit "new line" and "new paragraph".
/// A phrase counts as a command only when it stands on its own: at the start of the text or after
/// sentence/clause punctuation, and followed by the end of the text or a new sentence (capital letter,
/// digit or quote). Anything else ("add a new line of code") stays literal. When in doubt, literal wins.
/// </summary>
public static partial class SpokenFormatting
{
    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = Command().Replace(text, m =>
            m.Groups["cmd"].Value.Contains("paragraph", StringComparison.OrdinalIgnoreCase) ? "\n\n" : "\n");

        if (ReferenceEquals(result, text) || result == text) return text;
        return NormalizeBreaks(result);
    }

    /// <summary>Removes spaces around line breaks and caps runs at one blank line (avoids double formatting).</summary>
    public static string NormalizeBreaks(string text)
    {
        text = SpacesAroundBreak().Replace(text, "\n");
        text = ExtraBreaks().Replace(text, "\n\n");
        // Trim spaces (not line breaks) at the ends: a leading break is meaningful between segments.
        return text.Trim(' ', '\t');
    }

    // (start | after . ! ? , ; : or a line break) [spaces] command [optional punctuation]
    // (end | line break | spaces + capital/digit/quote). Only the command words are case-insensitive:
    // a global IgnoreCase would also make \p{Lu} match lowercase letters.
    [GeneratedRegex(@"(?:(?<=^)|(?<=[.!?,;:\n]))[ \t]*\b(?<cmd>(?i:new[ \t]+paragraph|new[ \t]*line))\b[.!?,;:]?(?=[ \t]*$|[ \t]*\n|[ \t]+[\p{Lu}\d""'“‘(])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Command();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpacesAroundBreak();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBreaks();
}
