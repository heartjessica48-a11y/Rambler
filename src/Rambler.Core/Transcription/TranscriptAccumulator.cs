using System.Text;

namespace Rambler.Core.Transcription;

/// <summary>
/// Collects transcription events for one live connection.
/// <list type="bullet">
/// <item>Interim results are provisional: each one replaces the previous interim (display only).</item>
/// <item>Final results are committed. Whether the server sends a turn's finals as deltas or as a growing
/// cumulative text, both are handled without duplication: a final that extends the current turn's text
/// replaces it; an exact repeat is ignored; anything else is appended.</item>
/// <item>Committed text is never rewritten by later interim results.</item>
/// </list>
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class TranscriptAccumulator
{
    private readonly List<string> _sealedTurns = [];
    private readonly List<string> _turnSegments = [];

    public string Interim { get; private set; } = string.Empty;

    public bool HasFinalText => _sealedTurns.Count > 0 || _turnSegments.Count > 0;

    public void OnInterim(string text) => Interim = text.Trim();

    public void OnFinal(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return;

        var turnText = CurrentTurnText;
        if (turnText.Length > 0)
        {
            if (string.Equals(t, turnText, StringComparison.Ordinal) ||
                (_turnSegments.Count > 0 && string.Equals(t, _turnSegments[^1], StringComparison.Ordinal)))
            {
                Interim = string.Empty;
                return; // duplicate delivery
            }

            if (t.StartsWith(turnText, StringComparison.Ordinal))
            {
                // Cumulative update of the same turn: replace rather than append.
                _turnSegments.Clear();
                _turnSegments.Add(t);
                Interim = string.Empty;
                return;
            }
        }

        _turnSegments.Add(t);
        Interim = string.Empty;
    }

    /// <summary>The server finished a turn; later finals start a new turn.</summary>
    public void OnTurnComplete()
    {
        if (_turnSegments.Count > 0) _sealedTurns.Add(CurrentTurnText);
        _turnSegments.Clear();
        Interim = string.Empty;
    }

    private string CurrentTurnText => TranscriptJoiner.Join(_turnSegments);

    /// <summary>Committed (final) text only.</summary>
    public string FinalText
    {
        get
        {
            var turns = new List<string>(_sealedTurns.Count + 1);
            turns.AddRange(_sealedTurns);
            if (_turnSegments.Count > 0) turns.Add(CurrentTurnText);
            return TranscriptJoiner.Join(turns);
        }
    }

    /// <summary>Committed text plus the provisional tail, for the live preview.</summary>
    public string DisplayText
    {
        get
        {
            var final = FinalText;
            if (Interim.Length == 0) return final;

            // If the interim hypothesis covers the whole current turn, show it instead of the turn.
            var turnText = CurrentTurnText;
            if (turnText.Length > 0 && Interim.StartsWith(turnText, StringComparison.Ordinal))
            {
                var sealedText = TranscriptJoiner.Join(_sealedTurns);
                return TranscriptJoiner.Join([sealedText, Interim]);
            }
            return TranscriptJoiner.Join([final, Interim]);
        }
    }

    /// <summary>Best available text when finalization didn't complete: finals, else the last interim.</summary>
    public string BestEffortText => HasFinalText ? FinalText : Interim;
}

public static class TranscriptJoiner
{
    /// <summary>Joins segments with a single space unless whitespace/punctuation already separates them.</summary>
    public static string Join(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var raw in segments)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var seg = raw;
            if (sb.Length > 0)
            {
                var prev = sb[^1];
                var next = seg[0];
                var needsSpace = !char.IsWhiteSpace(prev) && !char.IsWhiteSpace(next) &&
                                 next is not ('.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}');
                if (needsSpace) sb.Append(' ');
            }
            sb.Append(seg);
        }
        return sb.ToString().Trim();
    }
}
