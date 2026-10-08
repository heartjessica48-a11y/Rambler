using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;
using Rambler.Core.Audio;
using Rambler.Core.Gemini;

namespace Rambler.Core.Transcription;

/// <summary>
/// Client-side utterance endpointing. When set, a natural pause ends the current utterance: its audio is
/// finalized on its own Live connection (activityEnd) while new audio continues on a fresh one. This makes
/// stable text available during dictation without relying on undocumented mid-turn finalization.
/// Thresholds are deliberately conservative so brief thinking pauses don't split a sentence.
/// </summary>
public sealed record PauseEndpointing
{
    /// <summary>Silence that ends an utterance.</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(1200);
    /// <summary>Utterances shorter than this are never split.</summary>
    public TimeSpan MinUtterance { get; init; } = TimeSpan.FromSeconds(2.5);
    /// <summary>After this long, a shorter pause is enough to split.</summary>
    public TimeSpan LongUtterance { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan LongUtterancePause { get; init; } = TimeSpan.FromMilliseconds(450);
    /// <summary>Hard cap so text still appears during non-stop speech.</summary>
    public TimeSpan MaxUtterance { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>RMS (0..1) at or above which audio counts as speech (≈ -40 dBFS).</summary>
    public double SpeechRms { get; init; } = 0.01;
}

public sealed record TranscriptionOptions(
    LiveSetupOptions Live,
    RecordedTranscriptionOptions Recorded,
    bool UseRecordedFallback)
{
    /// <summary>Live connections last ~10 minutes; roll over to a fresh connection before that.</summary>
    public TimeSpan RolloverAfter { get; init; } = TimeSpan.FromMinutes(9);
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(12);
    /// <summary>How long to wait for the final transcript after activityEnd.</summary>
    public TimeSpan FinalizeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxReconnectAttempts { get; init; } = 3;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Null = one utterance per connection (end-of-recording behavior).</summary>
    public PauseEndpointing? Endpointing { get; init; }
}

public sealed record TranscriptionResult(string Text, IReadOnlyList<string> Warnings, bool UsedRecordedFallback);

/// <summary>Stable, finalized text for one utterance. <see cref="Index"/> is unique and increasing per session.</summary>
public sealed record CommittedSegment(int Index, string Text, TimeSpan Duration);

public interface ITranscriptionSession : IAsyncDisposable
{
    /// <summary>Live preview text (committed + provisional). Raised on background threads.</summary>
    event Action<string>? PreviewChanged;

    /// <summary>
    /// Finalized text for one utterance, raised exactly once per non-empty utterance and strictly in order
    /// (a later utterance is held back until every earlier one is resolved). Raised on background threads.
    /// </summary>
    event Action<CommittedSegment>? Committed;

    /// <summary>Non-fatal status for the user, e.g. "reconnecting".</summary>
    event Action<string>? Notice;

    /// <summary>Unrecoverable failure (invalid key, quota, no way to transcribe). Recording should stop.</summary>
    event Action<GeminiException>? Fatal;

    void Start();

    /// <summary>Feeds 16 kHz mono PCM16. Called from the audio thread; never blocks on the network.</summary>
    void WriteAudio(ReadOnlySpan<byte> pcm);

    /// <summary>Ends the audio stream, emits all remaining committed segments, and returns the full transcript.</summary>
    Task<TranscriptionResult> CompleteAsync(CancellationToken ct);
}

public interface ITranscriptionSessionFactory
{
    ITranscriptionSession Create(TranscriptionOptions options, string apiKey);
}

public sealed class LiveTranscriptionSessionFactory(IWebSocketFactory sockets, IRecordedTranscriber recorded)
    : ITranscriptionSessionFactory
{
    public ITranscriptionSession Create(TranscriptionOptions options, string apiKey) =>
        new LiveTranscriptionSession(options, apiKey, sockets, recorded);
}

/// <summary>
/// One dictation's transcription. Audio is split into <em>segments</em> (utterances), one per Live
/// connection; a new segment starts at a natural pause (when endpointing is on), on rollover, or after a
/// connection failure. Each segment resolves to final text independently (live finals, or the
/// recorded-audio fallback for a failed segment) and is emitted in order as a <see cref="CommittedSegment"/>.
/// Audio kept for fallback lives in memory only and is zeroed when the session ends.
/// </summary>
public sealed class LiveTranscriptionSession : ITranscriptionSession
{
    private const int SendChunkBytes = 3200; // 100 ms of 16 kHz mono PCM16
    private const int MinUsefulAudioBytes = 8000; // 250 ms
    private const double BytesPerMs = 32.0;

    private readonly TranscriptionOptions _options;
    private readonly string _apiKey;
    private readonly IWebSocketFactory _sockets;
    private readonly IRecordedTranscriber _recorded;
    private readonly CancellationTokenSource _abort = new();
    private readonly object _lock = new();
    private readonly object _emitLock = new();
    private readonly List<Segment> _segments = [];
    private Segment _current;
    private Task _runner = Task.CompletedTask;
    private volatile bool _liveEnabled = true;
    private volatile GeminiException? _fatal;
    private bool _stopping;
    private bool _started;
    private int _disposed;
    private int _emitted;
    private int _nextToStream; // segments are streamed strictly in order (several can queue up during a reconnect)

    // Endpointing state for the current utterance (guarded by _lock).
    private double _utteranceMs;
    private double _silenceMs;
    private bool _heardSpeech;

    public LiveTranscriptionSession(TranscriptionOptions options, string apiKey, IWebSocketFactory sockets,
        IRecordedTranscriber recorded)
    {
        _options = options;
        _apiKey = apiKey;
        _sockets = sockets;
        _recorded = recorded;
        _current = NewSegment();
    }

    public event Action<string>? PreviewChanged;
    public event Action<CommittedSegment>? Committed;
    public event Action<string>? Notice;
    public event Action<GeminiException>? Fatal;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        _runner = Task.Run(RunAsync);
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty) return;
        lock (_lock)
        {
            if (_stopping) return;
            _current.Append(pcm);
            if (_options.Endpointing is { } ep && ShouldEndUtterance(ep, pcm)) RolloverLocked(_current);
        }
    }

    /// <summary>Caller holds the lock. Tracks speech/silence and decides whether the utterance is complete.</summary>
    private bool ShouldEndUtterance(PauseEndpointing ep, ReadOnlySpan<byte> pcm)
    {
        var ms = pcm.Length / BytesPerMs;
        _utteranceMs += ms;
        if (Rms(pcm) >= ep.SpeechRms)
        {
            _heardSpeech = true;
            _silenceMs = 0;
        }
        else
        {
            _silenceMs += ms;
        }

        if (!_heardSpeech) return false;
        return (_utteranceMs >= ep.MinUtterance.TotalMilliseconds && _silenceMs >= ep.Pause.TotalMilliseconds)
               || (_utteranceMs >= ep.LongUtterance.TotalMilliseconds && _silenceMs >= ep.LongUtterancePause.TotalMilliseconds)
               || _utteranceMs >= ep.MaxUtterance.TotalMilliseconds;
    }

    internal static double Rms(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0) return 0;
        double sum = 0;
        for (var i = 0; i < samples; i++)
        {
            double s = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * 2, 2)) / 32768.0;
            sum += s * s;
        }
        return Math.Sqrt(sum / samples);
    }

    public async Task<TranscriptionResult> CompleteAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            _stopping = true;
            _current.CompleteAudio();
        }

        using var reg = ct.Register(() => _abort.Cancel());
        await SwallowAsync(_runner).ConfigureAwait(false);

        Segment[] segments;
        lock (_lock) segments = [.. _segments];
        foreach (var s in segments)
        {
            if (!s.ResolutionScheduled)
            {
                s.Offline = true; // never streamed: transcribe its recording rather than drop it
                ScheduleResolution(s, Task.CompletedTask);
            }
            await SwallowAsync(s.Resolution).ConfigureAwait(false);
        }
        EmitResolved();
        ct.ThrowIfCancellationRequested();

        var texts = segments.Select(s => s.ResolvedText).Where(t => t.Length > 0).ToList();
        var warnings = segments.Select(s => s.Warning).OfType<string>().Distinct().ToList();
        var usedFallback = segments.Any(s => s.UsedFallback);

        ClearRecordings();
        return new TranscriptionResult(TranscriptJoiner.Join(texts), warnings, usedFallback);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_lock)
        {
            _stopping = true;
            _current.CompleteAudio();
        }
        _abort.Cancel();
        await SwallowAsync(_runner).ConfigureAwait(false);
        Segment[] segments;
        lock (_lock) segments = [.. _segments];
        foreach (var s in segments)
        {
            await SwallowAsync(s.Finalization).ConfigureAwait(false);
            await SwallowAsync(s.Resolution).ConfigureAwait(false);
            s.ReleaseQueuedAudio();
        }
        ClearRecordings();
        _abort.Dispose();
    }

    private Segment NewSegment()
    {
        var seg = new Segment(_segments.Count, _options.UseRecordedFallback);
        _segments.Add(seg);
        return seg;
    }

    private void RaisePreview()
    {
        var handler = PreviewChanged;
        if (handler is null) return;
        Segment[] segments;
        lock (_lock) segments = [.. _segments];
        var parts = new List<string>(segments.Length);
        foreach (var s in segments)
            lock (s.Transcript) parts.Add(s.Transcript.DisplayText);
        handler(TranscriptJoiner.Join(parts));
    }

    /// <summary>Starts a fresh segment for new audio; the old one finalizes in the background.</summary>
    private void RequestRollover(Segment seg)
    {
        lock (_lock) RolloverLocked(seg);
    }

    /// <summary>Caller holds the lock.</summary>
    private void RolloverLocked(Segment seg)
    {
        if (_stopping || !ReferenceEquals(seg, _current)) return;
        _current = NewSegment();
        seg.CompleteAudio();
        _utteranceMs = 0;
        _silenceMs = 0;
        _heardSpeech = false;
    }

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_abort.IsCancellationRequested)
        {
            Segment seg;
            lock (_lock)
            {
                if (_nextToStream >= _segments.Count) return;
                seg = _segments[_nextToStream++];
            }

            if (!_liveEnabled)
            {
                seg.Offline = true;
                await seg.DrainAsync(_abort.Token).ConfigureAwait(false);
                ScheduleResolution(seg, Task.CompletedTask); // transcribed from the recording
            }
            else if (!await StreamSegmentAsync(seg).ConfigureAwait(false))
            {
                if (_abort.IsCancellationRequested) return;
                var error = seg.Error!;
                AppLog.Warn($"Live segment {seg.Index} failed: {error.Kind}: {error.Message}");

                if (error.IsFatal)
                {
                    _fatal = error;
                    _liveEnabled = false;
                    Fatal?.Invoke(error);
                }
                else if (error.Kind is GeminiErrorKind.ModelUnavailable or GeminiErrorKind.BadRequest || ++failures > _options.MaxReconnectAttempts)
                {
                    _liveEnabled = false;
                    if (_options.UseRecordedFallback)
                        Notice?.Invoke("Live preview unavailable. Still recording; will transcribe when you stop.");
                    else
                        Fatal?.Invoke(error);
                }
                else
                {
                    Notice?.Invoke("Connection interrupted. Reconnecting…");
                }

                lock (_lock)
                {
                    if (!_stopping && ReferenceEquals(seg, _current))
                    {
                        _current = NewSegment();
                        seg.CompleteAudio();
                    }
                }
                await seg.DrainAsync(_abort.Token).ConfigureAwait(false);
                ScheduleResolution(seg, Task.CompletedTask); // fallback runs now, not at stop

                if (_liveEnabled)
                {
                    try { await Task.Delay(_options.ReconnectDelay * failures, _abort.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            else
            {
                failures = 0;
                ScheduleResolution(seg, seg.Finalization);
            }

            lock (_lock)
            {
                if (_stopping && _nextToStream >= _segments.Count) return;
            }
        }
    }

    // ---- Segment resolution and ordered emission ---------------------------------------------

    private void ScheduleResolution(Segment seg, Task finalization)
    {
        lock (seg)
        {
            if (seg.ResolutionScheduled) return;
            seg.ResolutionScheduled = true;
            seg.Resolution = Task.Run(async () =>
            {
                await SwallowAsync(finalization).ConfigureAwait(false);
                try
                {
                    await ResolveAsync(seg).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Resolve segment", ex);
                }
                finally
                {
                    seg.IsResolved = true;
                    EmitResolved();
                }
            });
        }
    }

    /// <summary>Decides the final text for one segment: live finals, or the recorded-audio fallback.</summary>
    private async Task ResolveAsync(Segment seg)
    {
        string text;
        lock (seg.Transcript) text = seg.Transcript.BestEffortText;

        if (seg.NeedsFallback && seg.AudioBytes >= MinUsefulAudioBytes)
        {
            // After a fatal error (bad key, quota) the same key can't succeed with the fallback either.
            if (_options.UseRecordedFallback && _fatal is null && seg.Recording is { Length: > 0 })
            {
                try
                {
                    byte[] wav;
                    lock (_lock) wav = WavWriter.ToWav(seg.Recording.GetBuffer().AsSpan(0, (int)seg.Recording.Length));
                    text = (await _recorded.TranscribeAsync(wav, _options.Recorded, _apiKey, _abort.Token).ConfigureAwait(false)).Trim();
                    Array.Clear(wav);
                    seg.UsedFallback = true;
                }
                catch (GeminiException ex)
                {
                    AppLog.Warn("Recorded-audio fallback failed: " + ex.Message);
                    seg.Warning = text.Length > 0
                        ? "Part of the recording may be incomplete (fallback failed: " + ex.UserMessage + ")"
                        : "Part of the recording couldn't be transcribed: " + ex.UserMessage;
                }
                catch (OperationCanceledException)
                {
                    // Session cancelled: keep what live transcription produced.
                }
            }
            else
            {
                seg.Warning = text.Length > 0
                    ? "Live transcription didn't finish; part of the text may be missing or provisional."
                    : "Part of the recording couldn't be transcribed.";
            }
        }
        else if (seg.TimedOut && text.Length > 0)
        {
            seg.Warning = "The final transcript arrived late; the last words may be provisional.";
        }

        seg.ResolvedText = text;
    }

    /// <summary>Emits resolved segments strictly in order; a pending earlier segment holds back later ones.</summary>
    private void EmitResolved()
    {
        lock (_emitLock)
        {
            while (true)
            {
                Segment seg;
                lock (_lock)
                {
                    if (_emitted >= _segments.Count) return;
                    seg = _segments[_emitted];
                }
                if (!seg.IsResolved) return;
                _emitted++;
                if (seg.ResolvedText.Length == 0) continue;
                try
                {
                    Committed?.Invoke(new CommittedSegment(seg.Index, seg.ResolvedText,
                        TimeSpan.FromMilliseconds(seg.AudioBytes / BytesPerMs)));
                }
                catch (Exception ex)
                {
                    AppLog.Error("Committed handler", ex);
                }
            }
        }
    }

    private async Task<bool> StreamSegmentAsync(Segment seg)
    {
        GeminiLiveConnection conn;
        try
        {
            conn = await GeminiLiveConnection.OpenAsync(_sockets, _apiKey, _options.Live, _options.SetupTimeout, _abort.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            seg.Fail(new GeminiException(GeminiErrorKind.Timeout, "Cancelled."));
            return false;
        }
        catch (GeminiException ex)
        {
            seg.Fail(ex);
            return false;
        }

        var receiveTask = ReceiveLoopAsync(conn, seg);
        using var rolloverTimer = new Timer(_ => RequestRollover(seg), null, _options.RolloverAfter, Timeout.InfiniteTimeSpan);
        var sentAudio = false;

        try
        {
            var reader = seg.Queue.Reader;
            while (await reader.WaitToReadAsync(_abort.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var chunk))
                {
                    try
                    {
                        if (!sentAudio)
                        {
                            await conn.SendAsync(LiveProtocol.ActivityStart, _abort.Token).ConfigureAwait(false);
                            sentAudio = true;
                        }
                        await conn.SendAsync(LiveProtocol.BuildAudio(chunk.Span), _abort.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        chunk.Return();
                    }
                }

                if (receiveTask.IsCompleted) throw seg.Error ?? conn.CloseError();
            }

            if (!sentAudio)
            {
                await conn.CloseAsync().ConfigureAwait(false);
                await conn.DisposeAsync().ConfigureAwait(false);
                await SwallowAsync(receiveTask).ConfigureAwait(false);
                return true; // no audio for this segment
            }

            seg.EndSent = true; // before sending, so a fast final reply is never missed
            await conn.SendAsync(LiveProtocol.ActivityEnd, _abort.Token).ConfigureAwait(false);
            seg.Finalization = FinalizeAsync(conn, seg, receiveTask);
            return true;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !_abort.IsCancellationRequested)
                seg.Fail(seg.Error ?? GeminiLiveConnection.MapStreamException(ex));
            else
                seg.Fail(new GeminiException(GeminiErrorKind.Timeout, "Cancelled."));
            await conn.DisposeAsync().ConfigureAwait(false);
            await SwallowAsync(receiveTask).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(GeminiLiveConnection conn, Segment seg)
    {
        try
        {
            while (true)
            {
                var events = await conn.ReceiveEventsAsync(_abort.Token).ConfigureAwait(false);
                if (events is null)
                {
                    if (!seg.TurnDone.Task.IsCompleted) seg.SetError(conn.CloseError());
                    return;
                }

                var changed = false;
                foreach (var e in events)
                {
                    switch (e.Kind)
                    {
                        case LiveEventKind.Interim:
                            lock (seg.Transcript) seg.Transcript.OnInterim(e.Text);
                            changed = true;
                            break;
                        case LiveEventKind.Final:
                            lock (seg.Transcript) seg.Transcript.OnFinal(e.Text);
                            changed = true;
                            if (e.Finished && seg.EndSent) seg.TurnDone.TrySetResult();
                            break;
                        case LiveEventKind.TurnComplete:
                            lock (seg.Transcript) seg.Transcript.OnTurnComplete();
                            changed = true;
                            if (seg.EndSent) seg.TurnDone.TrySetResult();
                            break;
                        case LiveEventKind.GoAway:
                            RequestRollover(seg);
                            break;
                    }
                }
                if (changed) RaisePreview();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_abort.IsCancellationRequested)
        {
            if (!seg.TurnDone.Task.IsCompleted) seg.SetError(GeminiLiveConnection.MapStreamException(ex));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FinalizeAsync(GeminiLiveConnection conn, Segment seg, Task receiveTask)
    {
        try
        {
            var timeout = Task.Delay(_options.FinalizeTimeout, _abort.Token);
            var done = await Task.WhenAny(seg.TurnDone.Task, receiveTask, timeout).ConfigureAwait(false);
            if (done == timeout) seg.TimedOut = true;
        }
        catch
        {
            // fall through to cleanup
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            await conn.DisposeAsync().ConfigureAwait(false);
            await SwallowAsync(receiveTask).ConfigureAwait(false);
            RaisePreview();
        }
    }

    private void ClearRecordings()
    {
        Segment[] segments;
        lock (_lock) segments = [.. _segments];
        foreach (var s in segments) lock (_lock) s.ClearRecording();
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* errors are recorded on segments */ }
    }

    private readonly record struct AudioChunk(byte[] Buffer, int Length)
    {
        public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
        public void Return() => ArrayPool<byte>.Shared.Return(Buffer, clearArray: true);
    }

    private sealed class Segment(int index, bool keepRecording)
    {
        private byte[]? _pending;
        private int _pendingLength;

        public int Index { get; } = index;
        public Channel<AudioChunk> Queue { get; } = Channel.CreateUnbounded<AudioChunk>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        public MemoryStream? Recording { get; private set; } = keepRecording ? new MemoryStream() : null;
        public TranscriptAccumulator Transcript { get; } = new();
        public TaskCompletionSource TurnDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Finalization { get; set; } = Task.CompletedTask;
        public long AudioBytes { get; private set; }
        public volatile bool EndSent;
        public volatile bool TimedOut;
        public volatile bool Offline;
        public GeminiException? Error { get; private set; }
        private volatile bool _failed;

        // Resolution (set once, read after IsResolved).
        public bool ResolutionScheduled { get; set; }
        public Task Resolution { get; set; } = Task.CompletedTask;
        public volatile bool IsResolved;
        public string ResolvedText { get; set; } = string.Empty;
        public string? Warning { get; set; }
        public bool UsedFallback { get; set; }

        public bool NeedsFallback => _failed || Offline || (TimedOut && !Transcript.HasFinalText) ||
                                     (Error is not null && !Transcript.HasFinalText);

        /// <summary>Called under the session lock.</summary>
        public void Append(ReadOnlySpan<byte> pcm)
        {
            AudioBytes += pcm.Length;
            Recording?.Write(pcm);

            while (!pcm.IsEmpty)
            {
                _pending ??= ArrayPool<byte>.Shared.Rent(SendChunkBytes);
                var n = Math.Min(SendChunkBytes - _pendingLength, pcm.Length);
                pcm[..n].CopyTo(_pending.AsSpan(_pendingLength));
                _pendingLength += n;
                pcm = pcm[n..];
                if (_pendingLength >= SendChunkBytes) FlushPending();
            }
        }

        /// <summary>Called under the session lock: flushes buffered audio and closes the queue.</summary>
        public void CompleteAudio()
        {
            FlushPending();
            Queue.Writer.TryComplete();
        }

        private void FlushPending()
        {
            if (_pending is null) return;
            if (_pendingLength > 0 && Queue.Writer.TryWrite(new AudioChunk(_pending, _pendingLength)))
            {
                _pending = null;
            }
            else
            {
                ArrayPool<byte>.Shared.Return(_pending, clearArray: true);
                _pending = null;
            }
            _pendingLength = 0;
        }

        public void Fail(GeminiException error)
        {
            Error ??= error;
            _failed = true;
        }

        public void SetError(GeminiException error) => Error ??= error;

        public async Task DrainAsync(CancellationToken ct)
        {
            try
            {
                while (await Queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    while (Queue.Reader.TryRead(out var chunk)) chunk.Return();
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void ReleaseQueuedAudio()
        {
            while (Queue.Reader.TryRead(out var chunk)) chunk.Return();
        }

        public void ClearRecording()
        {
            if (Recording is null) return;
            Array.Clear(Recording.GetBuffer());
            Recording.Dispose();
            Recording = null;
        }
    }
}
