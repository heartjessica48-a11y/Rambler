using Rambler.Core.Audio;
using Rambler.Core.Cleanup;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Transcription;

namespace Rambler.Core.Dictation;

/// <summary>
/// Owns one dictation at a time: microphone → transcription → optional cleanup → insertion.
/// All public methods are safe to call from the UI thread; long work runs on the thread pool.
/// <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class DictationCoordinator : IAsyncDisposable
{
    private const float VoiceLevelThreshold = 0.33f; // ≈ -40 dBFS on the meter scale

    private readonly IAudioSource _audio;
    private readonly ITranscriptionSessionFactory _sessions;
    private readonly ICleanupService _cleanup;
    private readonly ITextInserter _inserter;
    private readonly Func<AppSettings> _settings;
    private readonly Func<string?> _apiKey;
    private readonly object _lock = new();
    private readonly DictationStateMachine _sm = new();

    private volatile ITranscriptionSession? _session;
    private volatile ProgressiveOutput? _output;
    private int _sessionId;
    private volatile DictationState _state; // lock-free mirror for audio-thread reads
    private CancellationTokenSource? _cts;
    private Timer? _maxDurationTimer;
    private InsertionTarget _target = InsertionTarget.None;
    private AppSettings _snapshot = new();
    private string _key = string.Empty;
    private GeminiException? _fatal;
    private DateTime _lastVoiceUtc;
    private bool _heardVoice;
    private int _autoStopRequested;
    private bool _bypass;

    public DictationCoordinator(IAudioSource audio, ITranscriptionSessionFactory sessions, ICleanupService cleanup,
        ITextInserter inserter, Func<AppSettings> settings, Func<string?> apiKey)
    {
        _audio = audio;
        _sessions = sessions;
        _cleanup = cleanup;
        _inserter = inserter;
        _settings = settings;
        _apiKey = apiKey;

        _audio.DataAvailable += OnAudio;
        _audio.LevelChanged += OnLevel;
        _audio.Faulted += OnAudioFaulted;
    }

    public event Action? Changed;
    public event Action<float>? LevelChanged;

    /// <summary>Short user-facing notification (e.g. a tray balloon). Bool = is error.</summary>
    public event Action<string, bool>? Notify;

    /// <summary>Lock-free: safe from the audio thread (Stop() waits for that thread while holding the lock).</summary>
    public DictationState State => _state;
    public DictationMode? ActiveMode { get; private set; }
    public string Preview { get; private set; } = string.Empty;
    public string? Message { get; private set; }
    public bool MessageIsError { get; private set; }

    /// <summary>Completed text that was not inserted (cleanup or insertion failed). Offered for Insert/Copy.</summary>
    public string? PendingText { get; private set; }

    /// <summary>The most recent completed output, kept in memory only, for "Copy last result".</summary>
    public string? LastResult { get; private set; }

    public string TargetDescription => _target.Description;

    /// <summary>Where text goes: the field captured at start, or (follow-focus) the field currently in use.</summary>
    public InsertionTarget Target => _output?.CurrentTarget ?? _target;

    /// <summary>Progressive insertion status while text is being inserted as you speak; null otherwise.</summary>
    public OutputStatus? OutputState => _output?.Status;

    /// <summary>True when the last dictation finished and all its text was inserted.</summary>
    public bool LastCompleted { get; private set; }

    /// <summary>Increments for every dictation; used to ignore late events from an earlier one.</summary>
    public int SessionId => Volatile.Read(ref _sessionId);

    // ---- Commands -----------------------------------------------------------------------------

    public Task ToggleAsync(DictationTrigger trigger)
    {
        DictationState state;
        lock (_lock) state = _sm.State;
        if (state == DictationState.Listening) return StopAsync();
        if (state is DictationState.Idle or DictationState.Error) Start(trigger);
        return Task.CompletedTask; // busy finalizing/cleaning/inserting: ignore
    }

    /// <summary>Starts listening. <paramref name="target"/> overrides the captured foreground window.</summary>
    public bool Start(DictationTrigger trigger, InsertionTarget? target = null)
    {
        lock (_lock)
        {
            if (!_sm.CanStart) return false;

            var settings = _settings();
            var key = _apiKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                SetError("Add your Gemini API key in Settings › Gemini.");
                RaiseChanged();
                return false;
            }

            _snapshot = settings;
            _key = key.Trim();
            _target = target ?? _inserter.CaptureTarget();
            ActiveMode = DictationStateMachine.ResolveMode(trigger, settings.DefaultMode);
            _bypass = trigger == DictationTrigger.SmartBypass;
            PendingText = null;
            Preview = string.Empty;
            Message = null;
            MessageIsError = false;
            LastCompleted = false;
            _fatal = null;
            var sessionId = Interlocked.Increment(ref _sessionId);
            _heardVoice = false;
            _autoStopRequested = 0;
            _lastVoiceUtc = DateTime.UtcNow;

            var progressive = !settings.InsertWhenFinished;
            var session = _sessions.Create(BuildTranscriptionOptions(settings, progressive), _key);
            ProgressiveOutput? output = null;
            if (progressive)
            {
                output = new ProgressiveOutput(ActiveMode.Value, _target, _inserter, _cleanup, BuildCleanupOptions(settings), _key,
                    new ProgressiveOutputOptions { FollowFocus = settings.FollowFocus });
                output.Changed += () => { if (SessionId == sessionId) RaiseChanged(); };
                // Committed text flows to the output only for this dictation; a later one never consumes it.
                session.Committed += segment => { if (SessionId == sessionId) output.Add(segment); };
            }
            _output = output;

            // Ignore late events from a previous (cancelled) session.
            session.PreviewChanged += text => { if (ReferenceEquals(_session, session)) OnPreview(text); };
            session.Notice += text => { if (ReferenceEquals(_session, session)) OnNotice(text); };
            session.Fatal += ex => { if (ReferenceEquals(_session, session)) OnFatal(ex); };

            try
            {
                _session = session; // set first so no early audio is dropped
                _audio.Start(settings.MicrophoneDeviceId);
            }
            catch (Exception ex)
            {
                _session = null;
                _output = null;
                _ = session.DisposeAsync().AsTask();
                if (output is not null) _ = output.DisposeAsync().AsTask();
                AppLog.Error("Microphone start", ex);
                SetError(ex is AudioDeviceException ? ex.Message : "Couldn't start the microphone: " + ex.Message);
                RaiseChanged();
                return false;
            }

            session.Start();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            Transition(DictationState.Listening);
            _maxDurationTimer = new Timer(_ => AutoStop("Maximum recording length reached."), null,
                TimeSpan.FromMinutes(settings.MaxRecordingMinutes), Timeout.InfiniteTimeSpan);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The user switched the default mode. Applies immediately, including to a dictation that is still
    /// listening (unless it was started with the Smart Only bypass hotkey).
    /// </summary>
    public void ChangeMode(DictationMode mode)
    {
        lock (_lock)
        {
            if (_sm.State != DictationState.Listening || _bypass) return;
            ActiveMode = mode;
            _output?.SetMode(mode);
        }
        RaiseChanged();
    }

    /// <summary>Stops listening, then finalizes, optionally cleans up, and inserts.</summary>
    public async Task StopAsync()
    {
        ITranscriptionSession session;
        ProgressiveOutput? output;
        CancellationToken ct;
        DictationMode mode;
        lock (_lock)
        {
            if (_sm.State != DictationState.Listening || _session is null || _cts is null) return;
            output = _output;
            _audio.Stop(); // release the microphone immediately
            DisposeTimer();
            Transition(DictationState.Finalizing);
            session = _session;
            ct = _cts.Token;
            mode = ActiveMode ?? DictationMode.SmartOnly;
        }
        RaiseChanged();

        await RunPipelineAsync(session, output, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Discards the current dictation. Nothing is inserted.</summary>
    public async Task CancelAsync()
    {
        ITranscriptionSession? toDispose = null;
        ProgressiveOutput? outputToDispose = null;
        lock (_lock)
        {
            switch (_sm.State)
            {
                case DictationState.Listening:
                    _audio.Stop();
                    DisposeTimer();
                    toDispose = _session;
                    _session = null;
                    outputToDispose = _output;
                    _output = null;
                    Interlocked.Increment(ref _sessionId); // anything still in flight belongs to a dead session
                    _cts?.Cancel();
                    Transition(DictationState.Idle);
                    Message = "Dictation cancelled.";
                    MessageIsError = false;
                    break;
                case DictationState.Finalizing or DictationState.Cleaning:
                    _cts?.Cancel(); // the pipeline returns to Idle
                    break;
                default:
                    return;
            }
        }
        RaiseChanged();
        if (outputToDispose is not null) await outputToDispose.DisposeAsync().ConfigureAwait(false);
        if (toDispose is not null) await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Inserts <see cref="PendingText"/> (the user accepted the offer) where they were last typing: the text
    /// field focused before the popup opened, or the original target if that one can't take text.
    /// </summary>
    public async Task InsertPendingAsync()
    {
        string text;
        lock (_lock)
        {
            if (PendingText is null || _sm.State != DictationState.Error) return;
            text = PendingText;
            PendingText = null;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var current = _inserter.CaptureTarget();
            if (current.CanReceiveText) _target = current;
        }
        await InsertAsync(text, _cts.Token).ConfigureAwait(false);
    }

    public bool CopyPending() => PendingText is { } t && CopyAndReport(t);

    public bool CopyLastResult() => LastResult is { } t && CopyAndReport(t);

    public void Dismiss()
    {
        lock (_lock)
        {
            if (_sm.State == DictationState.Error)
            {
                Transition(DictationState.Idle);
                PendingText = null;
            }
            else if (_sm.State != DictationState.Idle)
            {
                return;
            }
            Message = null;
            MessageIsError = false;
        }
        RaiseChanged();
    }

    // ---- Pipeline ----------------------------------------------------------------------------

    private async Task RunPipelineAsync(ITranscriptionSession session, ProgressiveOutput? output, DictationMode mode,
        CancellationToken ct)
    {
        TranscriptionResult result;
        try
        {
            // Emits every remaining committed utterance (into the progressive output) before returning.
            result = await session.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposeSessionAsync(session, output).ConfigureAwait(false);
            ReturnToIdle("Dictation cancelled.");
            return;
        }
        catch (Exception ex)
        {
            await DisposeSessionAsync(session, output).ConfigureAwait(false);
            AppLog.Error("Transcription", ex);
            Fail(ex is GeminiException g ? g.UserMessage : "Transcription failed: " + ex.Message);
            return;
        }
        finally
        {
            lock (_lock) _session = null;
        }
        await session.DisposeAsync().ConfigureAwait(false);

        var text = result.Text.Trim();
        var warning = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;

        GeminiException? fatal;
        lock (_lock) fatal = _fatal;

        if (output is not null)
        {
            await FinishProgressiveAsync(output, text, warning, fatal, ct).ConfigureAwait(false);
            return;
        }

        if (text.Length == 0)
        {
            Fail(fatal?.UserMessage ?? warning ?? "No speech was recognized.");
            return;
        }

        LastResult = text;
        if (fatal is not null)
        {
            Offer(text, fatal.UserMessage + " The partial transcript is ready to insert or copy.");
            return;
        }

        var final = SpokenFormatting.Apply(text);
        if (mode == DictationMode.PromptCleanup)
        {
            if (!TryTransition(DictationState.Cleaning)) return;
            RaiseChanged();
            try
            {
                final = await _cleanup.CleanAsync(new CleanupRequest(text), BuildCleanupOptions(_snapshot), _key, ct).ConfigureAwait(false);
                LastResult = final;
            }
            catch (OperationCanceledException)
            {
                ReturnToIdle("Dictation cancelled.");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("Cleanup", ex);
                var reason = ex is GeminiException g ? g.UserMessage : ex.Message;
                Offer(text, $"Cleanup failed: {reason} Insert the SMART transcript instead?");
                return;
            }
        }

        await InsertAsync(final, ct, warning).ConfigureAwait(false);
    }

    /// <summary>Progressive mode: flush the output, then report what was inserted and keep anything that wasn't.</summary>
    private async Task FinishProgressiveAsync(ProgressiveOutput output, string transcript, string? warning,
        GeminiException? fatal, CancellationToken ct)
    {
        if (!TryTransition(DictationState.Inserting))
        {
            await output.DisposeAsync().ConfigureAwait(false);
            return;
        }
        RaiseChanged();

        OutputResult result;
        try
        {
            // Stopping from Rambler's popup took focus away from the target: allow one return of focus.
            result = await output.CompleteAsync(allowRefocus: _inserter.IsOwnWindowActive, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisposeOutputAsync(output).ConfigureAwait(false);
            ReturnToIdle("Dictation cancelled. Text already inserted stays as is.");
            return;
        }
        await DisposeOutputAsync(output).ConfigureAwait(false);

        var all = result.AllText.Trim();
        var pending = result.PendingText.Trim();
        if (all.Length == 0)
        {
            if (transcript.Length == 0) Fail(fatal?.UserMessage ?? warning ?? "No speech was recognized.");
            else Offer(transcript, "Cleanup returned no text. Insert the SMART transcript instead?");
            return;
        }

        LastResult = all;
        if (pending.Length > 0)
        {
            var why = result.Problem ?? fatal?.UserMessage ?? (_target.CanReceiveText
                ? $"{_target.DisplayName} wasn't focused, so the rest wasn't typed."
                : "No editable text field was focused when dictation started.");
            Offer(pending, why + " Insert puts it where you were last typing; Copy puts it on the clipboard.");
            return;
        }

        if (fatal is not null)
        {
            Fail(fatal.UserMessage + " Everything transcribed before that was inserted.");
            return;
        }

        lock (_lock)
        {
            Transition(DictationState.Idle);
            Message = warning;
            MessageIsError = false;
            LastCompleted = true;
        }
        RaiseChanged();
        if (warning is not null) Notify?.Invoke(warning, false);
    }

    private async Task DisposeOutputAsync(ProgressiveOutput output)
    {
        await output.DisposeAsync().ConfigureAwait(false);
        lock (_lock)
        {
            if (ReferenceEquals(_output, output)) _output = null;
        }
        RaiseChanged();
    }

    private async Task DisposeSessionAsync(ITranscriptionSession session, ProgressiveOutput? output)
    {
        if (output is not null) await DisposeOutputAsync(output).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task InsertAsync(string output, CancellationToken ct, string? warning = null)
    {
        if (!TryTransition(DictationState.Inserting)) return;
        if (_snapshot.FollowFocus)
        {
            var current = _inserter.CaptureTarget(); // where the user is now (Rambler's popup is skipped)
            if (current.CanReceiveText) _target = current;
        }
        RaiseChanged();

        InsertionResult result;
        try
        {
            result = await _inserter.InsertAsync(_target, output, InsertOptions.Interactive, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Insertion", ex);
            result = new InsertionResult(InsertionOutcome.Failed, "Couldn't insert the text: " + ex.Message);
        }

        switch (result.Outcome)
        {
            case InsertionOutcome.Inserted:
                var note = string.Join(" ", new[] { warning, result.Message }.Where(m => m is not null));
                lock (_lock)
                {
                    Transition(DictationState.Idle);
                    Message = note.Length > 0 ? note : null;
                    MessageIsError = false;
                    LastCompleted = true;
                }
                RaiseChanged();
                if (note.Length > 0) Notify?.Invoke(note, false);
                break;
            case InsertionOutcome.CopiedToClipboard:
                Offer(output, result.Message ?? "Text copied to the clipboard. Press Ctrl+V to paste.");
                break;
            case InsertionOutcome.TargetNotReady:
                // Part may already be typed; offer only the rest so nothing is inserted twice.
                var rest = output[Math.Clamp(result.CharsInserted, 0, output.Length)..].TrimStart();
                Offer(rest, (result.Message ?? "Focus moved.") + " The rest is kept: Insert or Copy it.");
                break;
            default:
                Offer(output, (result.Message ?? "Couldn't insert the text.") + " Use Copy to get it.");
                break;
        }
    }

    // ---- Event handlers ----------------------------------------------------------------------

    private void OnAudio(ReadOnlySpan<byte> pcm) => _session?.WriteAudio(pcm);

    private void OnLevel(float level)
    {
        LevelChanged?.Invoke(level);
        if (State != DictationState.Listening) return;

        var now = DateTime.UtcNow;
        if (level >= VoiceLevelThreshold)
        {
            _heardVoice = true;
            _lastVoiceUtc = now;
        }
        else if (_snapshot.AutoStopOnSilence && _heardVoice &&
                 now - _lastVoiceUtc > TimeSpan.FromSeconds(_snapshot.SilenceTimeoutSeconds))
        {
            AutoStop("Stopped after silence.");
        }
    }

    private void AutoStop(string reason)
    {
        if (Interlocked.Exchange(ref _autoStopRequested, 1) == 1) return;
        if (State != DictationState.Listening) return;
        Notify?.Invoke(reason, false);
        _ = Task.Run(StopAsync);
    }

    private void OnAudioFaulted(Exception ex)
    {
        AppLog.Error("Microphone", ex);
        if (State != DictationState.Listening) return;
        Notify?.Invoke("Microphone disconnected. Finishing with what was recorded.", true);
        _ = Task.Run(StopAsync);
    }

    private void OnPreview(string text)
    {
        Preview = text;
        RaiseChanged();
    }

    private void OnNotice(string notice)
    {
        Message = notice;
        MessageIsError = false;
        RaiseChanged();
    }

    private void OnFatal(GeminiException ex)
    {
        AppLog.Warn($"Fatal transcription error: {ex.Kind}: {ex.Message}");
        lock (_lock) _fatal ??= ex;
        Notify?.Invoke(ex.UserMessage, true);
        _ = Task.Run(StopAsync); // stop recording; keep whatever was transcribed
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <param name="progressive">Split utterances at natural pauses so stable text is available while speaking.</param>
    public static TranscriptionOptions BuildTranscriptionOptions(AppSettings s, bool progressive = false) => new(
        new LiveSetupOptions(s.LiveModel, Smart: true, s.LanguageCode, s.CustomVocabulary),
        new RecordedTranscriptionOptions(s.RecordedModel, Smart: true, s.LanguageCode, s.CustomVocabulary),
        s.UseRecordedFallback)
    {
        Endpointing = progressive ? new PauseEndpointing() : null,
    };

    public static CleanupOptions BuildCleanupOptions(AppSettings s) => new(
        s.CleanupModel, s.CleanupPrompt, s.TechnicalRefinement, s.CleanupThinkingLevel, s.CustomVocabulary);

    private bool TryTransition(DictationState to)
    {
        lock (_lock)
        {
            if (!_sm.CanTransition(to)) return false; // e.g. cancelled meanwhile
            Transition(to);
            return true;
        }
    }

    private void Offer(string text, string message)
    {
        lock (_lock)
        {
            PendingText = text;
            SetError(message);
        }
        RaiseChanged();
        Notify?.Invoke(message, true);
    }

    private void Fail(string message)
    {
        lock (_lock) SetError(message);
        RaiseChanged();
        Notify?.Invoke(message, true);
    }

    private void ReturnToIdle(string? message)
    {
        lock (_lock)
        {
            if (_sm.State != DictationState.Idle) Transition(DictationState.Idle);
            Message = message;
            MessageIsError = false;
        }
        RaiseChanged();
    }

    /// <summary>Caller holds the lock.</summary>
    private void SetError(string message)
    {
        Transition(DictationState.Error);
        Message = message;
        MessageIsError = true;
    }

    private bool CopyAndReport(string text)
    {
        var ok = _inserter.CopyToClipboard(text);
        Message = ok ? "Copied to the clipboard." : "The clipboard is busy. Try again.";
        MessageIsError = !ok;
        RaiseChanged();
        return ok;
    }

    private void DisposeTimer()
    {
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = null;
    }

    /// <summary>Caller holds the lock.</summary>
    private void Transition(DictationState to)
    {
        _sm.TransitionTo(to);
        _state = to;
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        ITranscriptionSession? session;
        ProgressiveOutput? output;
        lock (_lock)
        {
            _audio.Stop();
            DisposeTimer();
            _cts?.Cancel();
            session = _session;
            _session = null;
            output = _output;
            _output = null;
        }
        if (output is not null) await output.DisposeAsync().ConfigureAwait(false);
        _audio.DataAvailable -= OnAudio;
        _audio.LevelChanged -= OnLevel;
        _audio.Faulted -= OnAudioFaulted;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }
}
