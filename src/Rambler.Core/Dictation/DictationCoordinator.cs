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
            _fatal = null;
            _heardVoice = false;
            _autoStopRequested = 0;
            _lastVoiceUtc = DateTime.UtcNow;

            var session = _sessions.Create(BuildTranscriptionOptions(settings), _key);
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
                _ = session.DisposeAsync().AsTask();
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
        }
        RaiseChanged();
    }

    /// <summary>Stops listening, then finalizes, optionally cleans up, and inserts.</summary>
    public async Task StopAsync()
    {
        ITranscriptionSession session;
        CancellationToken ct;
        DictationMode mode;
        lock (_lock)
        {
            if (_sm.State != DictationState.Listening || _session is null || _cts is null) return;
            _audio.Stop(); // release the microphone immediately
            DisposeTimer();
            Transition(DictationState.Finalizing);
            session = _session;
            ct = _cts.Token;
            mode = ActiveMode ?? DictationMode.SmartOnly;
        }
        RaiseChanged();

        await RunPipelineAsync(session, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Discards the current dictation. Nothing is inserted.</summary>
    public async Task CancelAsync()
    {
        ITranscriptionSession? toDispose = null;
        lock (_lock)
        {
            switch (_sm.State)
            {
                case DictationState.Listening:
                    _audio.Stop();
                    DisposeTimer();
                    toDispose = _session;
                    _session = null;
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
        if (toDispose is not null) await toDispose.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Inserts <see cref="PendingText"/> into the original target (user accepted the offer).</summary>
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

    private async Task RunPipelineAsync(ITranscriptionSession session, DictationMode mode, CancellationToken ct)
    {
        TranscriptionResult result;
        try
        {
            result = await session.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            ReturnToIdle("Dictation cancelled.");
            return;
        }
        catch (Exception ex)
        {
            await session.DisposeAsync().ConfigureAwait(false);
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

        var output = text;
        if (mode == DictationMode.PromptCleanup)
        {
            if (!TryTransition(DictationState.Cleaning)) return;
            RaiseChanged();
            try
            {
                output = await _cleanup.CleanAsync(text, BuildCleanupOptions(_snapshot), _key, ct).ConfigureAwait(false);
                LastResult = output;
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

        await InsertAsync(output, ct, warning).ConfigureAwait(false);
    }

    private async Task InsertAsync(string output, CancellationToken ct, string? warning = null)
    {
        if (!TryTransition(DictationState.Inserting)) return;
        RaiseChanged();

        InsertionResult result;
        try
        {
            result = await _inserter.InsertAsync(_target, output, ct).ConfigureAwait(false);
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
                }
                RaiseChanged();
                if (note.Length > 0) Notify?.Invoke(note, false);
                break;
            case InsertionOutcome.CopiedToClipboard:
                Offer(output, result.Message ?? "Text copied to the clipboard. Press Ctrl+V to paste.");
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

    public static TranscriptionOptions BuildTranscriptionOptions(AppSettings s) => new(
        new LiveSetupOptions(s.LiveModel, Smart: true, s.LanguageCode, s.CustomVocabulary),
        new RecordedTranscriptionOptions(s.RecordedModel, Smart: true, s.LanguageCode, s.CustomVocabulary),
        s.UseRecordedFallback);

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
        lock (_lock)
        {
            _audio.Stop();
            DisposeTimer();
            _cts?.Cancel();
            session = _session;
            _session = null;
        }
        _audio.DataAvailable -= OnAudio;
        _audio.LevelChanged -= OnLevel;
        _audio.Faulted -= OnAudioFaulted;
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
    }
}
