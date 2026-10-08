using System.Text;
using System.Threading.Channels;
using Rambler.Core.Cleanup;
using Rambler.Core.Transcription;

namespace Rambler.Core.Dictation;

public enum OutputStatus
{
    Idle,
    /// <summary>Cleaning up a chunk.</summary>
    Processing,
    Inserting,
    /// <summary>The intended text field isn't focused; text is kept until it is (or the user copies it).</summary>
    WaitingForTarget,
    /// <summary>Insertion stopped after a failure; everything from here on is kept as pending text.</summary>
    Halted,
}

/// <param name="InsertedText">Text actually typed/pasted into the target.</param>
/// <param name="PendingText">Text that could not be inserted (kept for Insert/Copy).</param>
/// <param name="AllText">Everything produced, in order (inserted + pending).</param>
public sealed record OutputResult(string InsertedText, string PendingText, string AllText, string? Problem);

public sealed record ProgressiveOutputOptions
{
    public ThoughtBufferOptions Buffer { get; init; } = new();
    /// <summary>How often the thought buffer checks for idle time. Zero disables the timer (tests call Tick).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    /// <summary>Fallback re-check while waiting for the target (focus-change events wake it sooner).</summary>
    public TimeSpan TargetPollInterval { get; init; } = TimeSpan.FromMilliseconds(750);
    /// <summary>How much committed text is sent to cleanup as read-only context.</summary>
    public int ContextChars { get; init; } = 1200;
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// True: insert wherever the user is typing right now (re-captured before each insert).
    /// False: insert only into the field captured when dictation started.
    /// Either way, nothing is typed into Rambler's own windows or into non-editable targets.
    /// </summary>
    public bool FollowFocus { get; init; }
}

/// <summary>
/// Progressive output for one dictation: committed utterances in, inserted text out.
/// <list type="bullet">
/// <item>Smart Only: each utterance is formatted (explicit "new line"/"new paragraph") and inserted.</item>
/// <item>Prompt Cleanup: utterances are grouped by <see cref="ThoughtBuffer"/>, each chunk is cleaned with
/// recent committed text as read-only context, then inserted.</item>
/// </list>
/// A single worker handles chunks strictly in order, so a slow cleanup can never land after a later chunk,
/// and only one insertion runs at a time. Inserted text is never revisited. If the target isn't focused,
/// insertion waits (no focus stealing); once anything is pending, everything after it stays pending too,
/// so the order of text is never broken.
/// </summary>
public sealed class ProgressiveOutput : IAsyncDisposable
{
    private sealed record WorkItem(string Raw, bool Clean, bool IsFinal);

    private readonly InsertionTarget _target;
    private InsertionTarget _currentTarget;
    private readonly ITextInserter _inserter;
    private readonly ICleanupService _cleanup;
    private readonly CleanupOptions _cleanupOptions;
    private readonly string _apiKey;
    private readonly ProgressiveOutputOptions _options;
    private readonly ThoughtBuffer _buffer;
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _lock = new();
    private readonly StringBuilder _all = new();
    private readonly StringBuilder _inserted = new();
    private readonly StringBuilder _pending = new();
    private readonly Timer? _timer;
    private readonly Task _worker;
    private DictationMode _mode;
    private OutputStatus _status;
    private string? _problem;
    private bool _halted;
    private bool _completing;
    private bool _allowRefocus;
    private bool _refocusUsed;
    private int _pendingBreaks; // a spoken "new line/paragraph" waiting for the text that follows it
    private int _disposed;

    public ProgressiveOutput(DictationMode mode, InsertionTarget target, ITextInserter inserter, ICleanupService cleanup,
        CleanupOptions cleanupOptions, string apiKey, ProgressiveOutputOptions? options = null)
    {
        _mode = mode;
        _target = target;
        _currentTarget = target;
        _inserter = inserter;
        _cleanup = cleanup;
        _cleanupOptions = cleanupOptions;
        _apiKey = apiKey;
        _options = options ?? new ProgressiveOutputOptions();
        _buffer = new ThoughtBuffer(_options.Buffer);

        _inserter.FocusChanged += OnFocusChanged;
        _worker = Task.Run(WorkerAsync);
        if (_options.TickInterval > TimeSpan.Zero)
            _timer = new Timer(_ => Tick(), null, _options.TickInterval, _options.TickInterval);
    }

    /// <summary>Raised (on any thread) when <see cref="Status"/> or <see cref="Problem"/> changes.</summary>
    public event Action? Changed;

    public OutputStatus Status { get { lock (_lock) return _status; } }
    public string? Problem { get { lock (_lock) return _problem; } }
    public bool HasPending { get { lock (_lock) return _pending.Length > 0; } }

    /// <summary>Where text goes now: the starting field, or (follow-focus) the field last seen focused.</summary>
    public InsertionTarget CurrentTarget { get { lock (_lock) return _currentTarget; } }

    /// <summary>The user switched modes mid-dictation; applies to text committed from now on.</summary>
    public void SetMode(DictationMode mode)
    {
        lock (_lock) _mode = mode;
    }

    /// <summary>Accepts one committed utterance (any thread, never blocks).</summary>
    public void Add(CommittedSegment segment)
    {
        lock (_lock)
        {
            if (_completing || _disposed != 0)
            {
                AppLog.Warn($"Ignored segment {segment.Index} that arrived after output completed.");
                return;
            }

            if (_mode == DictationMode.SmartOnly)
            {
                // Anything buffered while in cleanup mode goes first, so order is kept.
                if (_buffer.Flush(isFinal: false) is { } leftover) Enqueue(new WorkItem(leftover.Text, Clean: true, IsFinal: false));
                Enqueue(new WorkItem(segment.Text, Clean: false, IsFinal: false));
            }
            else if (_buffer.Add(segment, _options.Clock()) is { } chunk)
            {
                Enqueue(new WorkItem(chunk.Text, Clean: true, IsFinal: false));
            }
        }
    }

    /// <summary>Releases buffered text after the speaker has been quiet (called by the timer).</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_completing || _disposed != 0) return;
            if (_buffer.Tick(_options.Clock()) is { } chunk) Enqueue(new WorkItem(chunk.Text, Clean: true, IsFinal: false));
        }
    }

    /// <summary>
    /// End of dictation: flushes the remaining text as the final chunk and waits until everything is inserted
    /// or kept as pending. <paramref name="allowRefocus"/> permits one attempt to return focus to the target
    /// (used when the user stopped from Rambler's own popup).
    /// </summary>
    public async Task<OutputResult> CompleteAsync(bool allowRefocus, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_completing)
            {
                _completing = true;
                _allowRefocus = allowRefocus;
                if (_buffer.Flush(isFinal: true) is { } last)
                    Enqueue(new WorkItem(last.Text, Clean: true, IsFinal: true)); // buffered text was spoken in cleanup mode
                _work.Writer.TryComplete();
            }
        }
        _timer?.Dispose();
        _wake.Release();

        using var reg = ct.Register(() => _cts.Cancel());
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        ct.ThrowIfCancellationRequested();

        lock (_lock) return new OutputResult(_inserted.ToString(), _pending.ToString(), _all.ToString(), _problem);
    }

    /// <summary>Caller holds the lock.</summary>
    private void Enqueue(WorkItem item) => _work.Writer.TryWrite(item);

    private async Task WorkerAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var text = await ProduceAsync(item, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // A segment that was only "new paragraph": apply it to whatever comes next.
                    var breaks = text?.Count(c => c == '\n') ?? 0;
                    if (breaks > 0) lock (_lock) _pendingBreaks = Math.Min(2, Math.Max(_pendingBreaks, breaks));
                    continue;
                }

                string piece;
                lock (_lock)
                {
                    if (_pendingBreaks > 0 && _all.Length > 0)
                        text = new string('\n', _pendingBreaks) + text.TrimStart('\n', '\r', ' ');
                    _pendingBreaks = 0;
                    piece = JoinPiece(_all, text);
                    if (piece.Length == 0) continue;
                    _all.Append(piece);
                }
                await DeliverAsync(piece, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Progressive output", ex);
            Halt("Insertion stopped unexpectedly: " + ex.Message);
        }
        finally
        {
            SetStatus(_halted ? OutputStatus.Halted : HasPending ? OutputStatus.WaitingForTarget : OutputStatus.Idle);
        }
    }

    /// <summary>Formats (Smart Only) or cleans (Prompt Cleanup) one work item.</summary>
    private async Task<string> ProduceAsync(WorkItem item, CancellationToken ct)
    {
        if (!item.Clean) return SpokenFormatting.Apply(item.Raw);

        SetStatus(OutputStatus.Processing);
        try
        {
            var request = new CleanupRequest(item.Raw, RecentContext(), Progressive: true, IsFinal: item.IsFinal);
            return await _cleanup.CleanAsync(request, _cleanupOptions, _apiKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Chunk cleanup", ex);
            var reason = ex is Gemini.GeminiException g ? g.UserMessage : ex.Message;
            Halt($"Cleanup failed ({reason}). The SMART transcript from that point is kept for you.");
            return item.Raw; // the underlying SMART text, never discarded
        }
    }

    private async Task DeliverAsync(string piece, CancellationToken ct)
    {
        var remaining = piece;
        while (remaining.Length > 0)
        {
            ct.ThrowIfCancellationRequested();

            var target = ResolveTarget();
            bool keep;
            lock (_lock) keep = _halted || _pending.Length > 0 || (!_options.FollowFocus && !target.CanReceiveText);
            if (keep)
            {
                lock (_lock) _pending.Append(remaining);
                SetStatus(_halted ? OutputStatus.Halted : OutputStatus.WaitingForTarget);
                return;
            }

            if (!target.CanReceiveText || !_inserter.IsTargetReady(target))
            {
                bool completing, refocus;
                lock (_lock)
                {
                    completing = _completing;
                    refocus = _allowRefocus && !_refocusUsed && target.CanReceiveText;
                    if (refocus) _refocusUsed = true;
                }

                if (!completing)
                {
                    SetStatus(OutputStatus.WaitingForTarget);
                    await _wake.WaitAsync(_options.TargetPollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                if (refocus)
                {
                    SetStatus(OutputStatus.Inserting);
                    var r = await _inserter.InsertAsync(target, remaining, new InsertOptions(AllowRefocus: true, CopyOnFailure: false), ct)
                        .ConfigureAwait(false);
                    remaining = Apply(r, remaining, completing: true);
                    continue;
                }

                lock (_lock) _pending.Append(remaining);
                SetStatus(OutputStatus.WaitingForTarget);
                return;
            }

            SetStatus(OutputStatus.Inserting);
            var result = await _inserter.InsertAsync(target, remaining, InsertOptions.Progressive, ct).ConfigureAwait(false);
            bool isCompleting;
            lock (_lock) isCompleting = _completing;
            if (_options.FollowFocus && !isCompleting && result.Outcome == InsertionOutcome.Failed)
            {
                // This app can't take text (e.g. it runs as administrator). Wait for the user to move on
                // instead of stopping insertion for the rest of the dictation.
                SetStatus(OutputStatus.WaitingForTarget);
                await _wake.WaitAsync(_options.TargetPollInterval, ct).ConfigureAwait(false);
                continue;
            }
            var before = remaining;
            remaining = Apply(result, remaining, isCompleting);
            if (remaining.Length > 0 && remaining == before && !isCompleting)
                await _wake.WaitAsync(_options.TargetPollInterval, ct).ConfigureAwait(false); // not ready: don't spin
        }
        SetStatus(OutputStatus.Idle);
    }

    private InsertionTarget ResolveTarget()
    {
        if (!_options.FollowFocus) return _target;
        var current = _inserter.CaptureTarget();
        lock (_lock)
        {
            if (current.CanReceiveText) _currentTarget = current;
        }
        return current;
    }

    /// <summary>Records an insertion result; returns the text still to insert.</summary>
    private string Apply(InsertionResult result, string remaining, bool completing)
    {
        switch (result.Outcome)
        {
            case InsertionOutcome.Inserted:
                lock (_lock) _inserted.Append(remaining);
                return string.Empty;

            case InsertionOutcome.TargetNotReady:
                var n = Math.Clamp(result.CharsInserted, 0, remaining.Length);
                lock (_lock)
                {
                    _inserted.Append(remaining, 0, n);
                    if (completing && n == 0)
                    {
                        _pending.Append(remaining); // at the end, don't wait: keep the rest for the user
                        return string.Empty;
                    }
                }
                return remaining[n..];

            default:
                Halt(result.Message ?? "Couldn't insert the text.");
                lock (_lock) _pending.Append(remaining);
                return string.Empty;
        }
    }

    /// <summary>Recent committed output (inserted or pending) for cleanup context, cut at a word boundary.</summary>
    private string RecentContext()
    {
        lock (_lock)
        {
            if (_all.Length <= _options.ContextChars) return _all.ToString().Trim();
            var tail = _all.ToString(_all.Length - _options.ContextChars, _options.ContextChars);
            var space = tail.IndexOfAny([' ', '\n']);
            return (space > 0 ? tail[(space + 1)..] : tail).Trim();
        }
    }

    /// <summary>Adds the separator between already-produced text and the next piece.</summary>
    internal static string JoinPiece(StringBuilder all, string text)
    {
        if (all.Length == 0) return text.TrimStart('\n', '\r', ' ');
        var first = text[0];
        var last = all[^1];
        if (first is '\n' || char.IsWhiteSpace(last)) return text;
        if (first is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']') return text;
        return " " + text;
    }

    private void Halt(string problem)
    {
        lock (_lock)
        {
            _halted = true;
            _problem ??= problem;
        }
        SetStatus(OutputStatus.Halted);
    }

    private void SetStatus(OutputStatus status)
    {
        lock (_lock)
        {
            if (_halted && status != OutputStatus.Halted && status != OutputStatus.Processing) status = OutputStatus.Halted;
            if (_status == status) return;
            _status = status;
        }
        Changed?.Invoke();
    }

    private void OnFocusChanged()
    {
        try { _wake.Release(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _inserter.FocusChanged -= OnFocusChanged;
        _timer?.Dispose();
        lock (_lock) _work.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch { /* cancelled */ }
        _cts.Dispose();
    }
}
