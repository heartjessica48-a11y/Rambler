using Rambler.Core.Transcription;

namespace Rambler.Core.Dictation;

/// <summary>Tuning for <see cref="ThoughtBuffer"/>. Defaults target roughly 5–15 s of speech per chunk.</summary>
public sealed record ThoughtBufferOptions
{
    /// <summary>A chunk ending at a complete sentence is released once it holds at least this much speech.</summary>
    public TimeSpan MinChunk { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Past this, a chunk is released unless it visibly trails off (comma, "and…").</summary>
    public TimeSpan TargetMaxChunk { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Always released past this, so text never waits indefinitely.</summary>
    public TimeSpan HardMaxChunk { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxChars { get; init; } = 900;
    /// <summary>The speaker paused this long after a complete sentence: release it even if short.</summary>
    public TimeSpan IdleFlush { get; init; } = TimeSpan.FromSeconds(2.5);
    /// <summary>The speaker paused this long: release whatever is buffered.</summary>
    public TimeSpan LongIdleFlush { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>A unit of committed speech sent to cleanup. Sequences are increasing per dictation.</summary>
public sealed record ThoughtChunk(int Sequence, string Text, TimeSpan Duration, int FirstSegment, int LastSegment, bool IsFinal);

/// <summary>
/// Groups committed utterances into thought-sized chunks with deterministic heuristics (no extra model):
/// sentence completion, accumulated speech length, idle time, and an explicit end of dictation.
/// A pause alone never ends a thought if the text visibly trails off. Not thread-safe; callers serialize.
/// </summary>
public sealed class ThoughtBuffer(ThoughtBufferOptions? options = null)
{
    private static readonly HashSet<string> s_continuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "like", "um", "uh", "the", "a", "an", "to", "of", "with",
        "which", "that", "then", "also", "if", "when", "while", "for", "my", "your",
    };

    private readonly ThoughtBufferOptions _options = options ?? new ThoughtBufferOptions();
    private readonly List<string> _parts = [];
    private TimeSpan _duration;
    private int _firstSegment = -1;
    private int _lastSegment = -1;
    private int _nextSequence;
    private DateTime _lastAdd;

    public bool IsEmpty => _parts.Count == 0;

    public string PendingText => TranscriptJoiner.Join(_parts);

    /// <summary>Adds a committed utterance; returns a chunk when a good boundary has been reached.</summary>
    public ThoughtChunk? Add(CommittedSegment segment, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(segment.Text)) return null;
        if (_parts.Count == 0) _firstSegment = segment.Index;
        _parts.Add(segment.Text.Trim());
        _lastSegment = segment.Index;
        _duration += segment.Duration;
        _lastAdd = now;

        var text = PendingText;
        var complete = IsComplete(text);
        if (_duration >= _options.HardMaxChunk || text.Length >= _options.MaxChars) return Take(false);
        if (_duration >= _options.MinChunk && complete) return Take(false);
        if (_duration >= _options.TargetMaxChunk && !TrailsOff(text)) return Take(false);
        return null;
    }

    /// <summary>Called periodically; releases text after the speaker has been quiet for a while.</summary>
    public ThoughtChunk? Tick(DateTime now)
    {
        if (_parts.Count == 0) return null;
        var idle = now - _lastAdd;
        if (idle >= _options.LongIdleFlush) return Take(false);
        if (idle >= _options.IdleFlush && IsComplete(PendingText)) return Take(false);
        return null;
    }

    /// <summary>End of dictation (or a mode switch): releases everything that is left.</summary>
    public ThoughtChunk? Flush(bool isFinal) => _parts.Count == 0 ? null : Take(isFinal);

    private ThoughtChunk Take(bool isFinal)
    {
        var chunk = new ThoughtChunk(_nextSequence++, PendingText, _duration, _firstSegment, _lastSegment, isFinal);
        _parts.Clear();
        _duration = TimeSpan.Zero;
        _firstSegment = _lastSegment = -1;
        return chunk;
    }

    /// <summary>Ends a sentence and doesn't trail off.</summary>
    public static bool IsComplete(string text)
    {
        var t = text.TrimEnd();
        if (t.Length == 0 || TrailsOff(t)) return false;
        var last = LastMeaningfulChar(t);
        return last is '.' or '!' or '?' or '…';
    }

    /// <summary>Ends with a comma/dash/ellipsis or a connective word: the thought is still going.</summary>
    public static bool TrailsOff(string text)
    {
        var t = text.TrimEnd();
        if (t.Length == 0) return false;
        if (t.EndsWith("...", StringComparison.Ordinal) || t.EndsWith('…')) return true;
        var last = LastMeaningfulChar(t);
        if (last is ',' or ';' or ':' or '-' or '–' or '—') return true;
        return s_continuationWords.Contains(LastWord(t));
    }

    private static char LastMeaningfulChar(string t)
    {
        for (var i = t.Length - 1; i >= 0; i--)
        {
            var c = t[i];
            if (c is '"' or '\'' or '”' or '’' or ')' or ']') continue;
            return c;
        }
        return '\0';
    }

    private static string LastWord(string t)
    {
        var end = t.Length;
        while (end > 0 && !char.IsLetterOrDigit(t[end - 1])) end--;
        var start = end;
        while (start > 0 && (char.IsLetter(t[start - 1]) || t[start - 1] == '\'')) start--;
        return t[start..end];
    }
}
