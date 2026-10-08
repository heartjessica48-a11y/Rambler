using Rambler.Core.Audio;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Tests.Fakes;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

public class DictationCoordinatorTests
{
    private readonly FakeAudioSource _audio = new();
    private readonly FakeSessionFactory _sessions = new();
    private readonly FakeCleanup _cleanup = new();
    private readonly FakeInserter _inserter = new();
    private AppSettings _settings = new();
    private string? _key = "AIzaTESTKEY0000000000000000000000000000";

    private DictationCoordinator Create() =>
        new(_audio, _sessions, _cleanup, _inserter, () => _settings.Clone(), () => _key);

    [Fact]
    public async Task Prompt_cleanup_runs_the_second_model_and_inserts_its_output()
    {
        var c = Create();
        Assert.True(c.Start(DictationTrigger.Default));
        Assert.Equal(DictationState.Listening, c.State);
        Assert.True(_audio.IsCapturing);

        await c.StopAsync();

        Assert.Equal(["hello world"], _cleanup.Inputs);
        var inserted = Assert.Single(_inserter.Inserted);
        Assert.Equal("CLEAN: hello world", inserted.Text);
        Assert.Equal(_inserter.Target, inserted.Target);
        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal("CLEAN: hello world", c.LastResult);
    }

    [Fact]
    public async Task Smart_only_inserts_the_smart_transcript_without_cleanup()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();

        Assert.Empty(_cleanup.Inputs);
        Assert.Equal("hello world", Assert.Single(_inserter.Inserted).Text);
    }

    [Fact]
    public async Task Bypass_hotkey_uses_smart_only_without_changing_the_default_mode()
    {
        _settings.DefaultMode = DictationMode.PromptCleanup;
        var c = Create();
        c.Start(DictationTrigger.SmartBypass);
        Assert.Equal(DictationMode.SmartOnly, c.ActiveMode);
        await c.StopAsync();

        Assert.Empty(_cleanup.Inputs);
        Assert.Equal("hello world", Assert.Single(_inserter.Inserted).Text);
        Assert.Equal(DictationMode.PromptCleanup, _settings.DefaultMode);

        c.Start(DictationTrigger.Default); // next normal dictation is back to cleanup
        Assert.Equal(DictationMode.PromptCleanup, c.ActiveMode);
    }

    [Fact]
    public async Task Switching_mode_while_listening_applies_immediately_but_not_to_bypass()
    {
        var c = Create();
        c.Start(DictationTrigger.Default);
        c.ChangeMode(DictationMode.SmartOnly);
        await c.StopAsync();
        Assert.Empty(_cleanup.Inputs);

        c.Start(DictationTrigger.SmartBypass);
        c.ChangeMode(DictationMode.PromptCleanup);
        Assert.Equal(DictationMode.SmartOnly, c.ActiveMode);
    }

    [Fact]
    public async Task Microphone_is_released_as_soon_as_dictation_stops()
    {
        var gate = new TaskCompletionSource();
        _sessions.Next = () => new FakeTranscriptionSession { CompletionGate = gate };
        var c = Create();
        c.Start(DictationTrigger.Default);

        var stopping = c.StopAsync();
        Assert.False(_audio.IsCapturing); // released before finalization finishes
        Assert.Equal(DictationState.Finalizing, c.State);
        gate.SetResult();
        await stopping;
        Assert.Equal(1, _audio.StopCount);
        Assert.True(_sessions.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Audio_is_forwarded_to_the_session_only_while_listening()
    {
        var c = Create();
        c.Start(DictationTrigger.Default);
        _audio.Emit(new byte[320]);
        await c.StopAsync();
        _audio.Emit(new byte[320]);
        Assert.Equal(320, _sessions.Sessions[0].AudioBytes);
    }

    [Fact]
    public async Task Cleanup_failure_offers_the_smart_transcript_instead_of_losing_it()
    {
        _cleanup.Throw = new GeminiException(GeminiErrorKind.Server, "boom");
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();

        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal("hello world", c.PendingText);
        Assert.Contains("SMART transcript", c.Message);
        Assert.Empty(_inserter.Inserted);

        await c.InsertPendingAsync();
        Assert.Equal("hello world", Assert.Single(_inserter.Inserted).Text);
        Assert.Null(c.PendingText);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Fact]
    public async Task Insertion_failure_keeps_the_text_for_manual_copy()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        _inserter.Result = _ => new InsertionResult(InsertionOutcome.CopiedToClipboard, "Window gone. Text copied.");
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();

        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal("hello world", c.PendingText);
        Assert.True(c.CopyPending());
        Assert.Equal(["hello world"], _inserter.Copied);
    }

    [Fact]
    public async Task Pending_text_is_inserted_at_most_once()
    {
        _cleanup.Throw = new GeminiException(GeminiErrorKind.Server, "boom");
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();

        await Task.WhenAll(c.InsertPendingAsync(), c.InsertPendingAsync());
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public void Missing_api_key_reports_an_error_and_never_opens_the_microphone()
    {
        _key = null;
        var c = Create();
        Assert.False(c.Start(DictationTrigger.Default));
        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal(0, _audio.StartCount);
        Assert.Empty(_sessions.Sessions);
    }

    [Fact]
    public void Microphone_errors_are_reported_and_the_session_is_discarded()
    {
        _audio.StartException = new AudioDeviceException(AudioDeviceError.AccessDenied, "Microphone access is blocked.");
        var c = Create();
        Assert.False(c.Start(DictationTrigger.Default));
        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal("Microphone access is blocked.", c.Message);
        Assert.True(_sessions.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Empty_transcript_is_an_error_and_inserts_nothing()
    {
        _sessions.Next = () => new FakeTranscriptionSession { Result = new TranscriptionResult("  ", [], false) };
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();

        Assert.Equal(DictationState.Error, c.State);
        Assert.Contains("No speech", c.Message);
        Assert.Empty(_inserter.Inserted);
        Assert.Empty(_cleanup.Inputs);
    }

    [Fact]
    public async Task Rapid_toggling_never_starts_overlapping_sessions_or_double_inserts()
    {
        var gate = new TaskCompletionSource();
        _sessions.Next = () => new FakeTranscriptionSession { CompletionGate = gate };
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();

        await c.ToggleAsync(DictationTrigger.Default); // start
        var stop = c.ToggleAsync(DictationTrigger.Default); // stop -> finalizing
        await c.ToggleAsync(DictationTrigger.Default); // ignored while busy
        await c.ToggleAsync(DictationTrigger.SmartBypass); // ignored while busy
        gate.SetResult();
        await stop;

        Assert.Single(_sessions.Sessions);
        Assert.Single(_inserter.Inserted);
        Assert.Equal(1, _audio.StartCount);
    }

    [Fact]
    public async Task Cancel_while_listening_discards_everything()
    {
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.CancelAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.False(_audio.IsCapturing);
        Assert.True(_sessions.Sessions[0].Disposed);
        Assert.Empty(_inserter.Inserted);
    }

    [Fact]
    public async Task Cancel_while_finalizing_returns_to_idle_without_inserting()
    {
        var gate = new TaskCompletionSource();
        _sessions.Next = () => new FakeTranscriptionSession { CompletionGate = gate };
        var c = Create();
        c.Start(DictationTrigger.Default);
        var stopping = c.StopAsync();
        await c.CancelAsync();
        await stopping;

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Empty(_inserter.Inserted);
    }

    [Fact]
    public async Task Fatal_transcription_error_stops_recording_and_offers_partial_text()
    {
        _settings.InsertWhenFinished = true; // end-of-recording mode: nothing is auto-inserted after a fatal error
        var session = new FakeTranscriptionSession { Result = new TranscriptionResult("partial words", [], false) };
        _sessions.Next = () => session;
        var c = Create();
        c.Start(DictationTrigger.Default);

        session.RaiseFatal(new GeminiException(GeminiErrorKind.QuotaExceeded, "quota"));
        await WaitFor(() => c.State == DictationState.Error);

        Assert.False(_audio.IsCapturing);
        Assert.Equal("partial words", c.PendingText);
        Assert.Empty(_inserter.Inserted); // never auto-inserted after a fatal error
        Assert.Empty(_cleanup.Inputs);
    }

    [Fact]
    public async Task Microphone_disconnect_finishes_with_what_was_recorded()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();
        c.Start(DictationTrigger.Default);
        _audio.Fault(new AudioDeviceException(AudioDeviceError.NotFound, "The microphone was disconnected."));
        await WaitFor(() => c.State == DictationState.Idle);

        Assert.Equal("hello world", Assert.Single(_inserter.Inserted).Text);
    }

    [Fact]
    public async Task Silence_auto_stop_triggers_only_when_enabled_and_after_speech()
    {
        _settings.AutoStopOnSilence = true;
        _settings.SilenceTimeoutSeconds = 2;
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();
        c.Start(DictationTrigger.Default);

        await Task.Delay(2100);
        _audio.EmitLevel(0.05f); // long silence before any speech never stops
        Assert.Equal(DictationState.Listening, c.State);

        _audio.EmitLevel(0.8f); // speech
        _audio.EmitLevel(0.05f); // a short thinking pause does not stop
        Assert.Equal(DictationState.Listening, c.State);

        await Task.Delay(2100);
        _audio.EmitLevel(0.05f); // silence longer than the timeout after speech
        await WaitFor(() => c.State == DictationState.Idle);
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public void Transcription_options_follow_settings()
    {
        _settings.LiveModel = "live-x";
        _settings.RecordedModel = "file-y";
        _settings.LanguageCode = "en-GB";
        _settings.CustomVocabulary = ["Rambler"];
        _settings.UseRecordedFallback = false;
        var o = DictationCoordinator.BuildTranscriptionOptions(_settings);

        Assert.Equal("live-x", o.Live.Model);
        Assert.True(o.Live.Smart);
        Assert.Equal("en-GB", o.Live.LanguageCode);
        Assert.Equal("file-y", o.Recorded.Model);
        Assert.False(o.UseRecordedFallback);
        Assert.Equal(["Rambler"], o.Live.CustomVocabulary);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}
