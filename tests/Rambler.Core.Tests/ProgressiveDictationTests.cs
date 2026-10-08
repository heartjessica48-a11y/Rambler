using System.Text.Json;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Tests.Fakes;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

internal static class Wait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }
}

public class SpokenFormattingTests
{
    [Theory]
    [InlineData("First point. New paragraph. Second point.", "First point.\n\nSecond point.")]
    [InlineData("Line one. New line. Line two.", "Line one.\nLine two.")]
    [InlineData("Line one, newline, Line two", "Line one,\nLine two")]
    [InlineData("New paragraph.", "\n\n")]
    [InlineData("new paragraph Next topic", "\n\nNext topic")]
    public void Explicit_commands_become_line_breaks(string input, string expected) =>
        Assert.Equal(expected, SpokenFormatting.Apply(input));

    [Theory]
    [InlineData("I need a new line of code here.")]
    [InlineData("We should start a new paragraph about that.")]
    [InlineData("Done. New line of products launches today.")] // lowercase continuation: talking about it
    [InlineData("The new paragraph tag is p.")]
    public void Talking_about_the_phrases_keeps_them_literal(string input) =>
        Assert.Equal(input, SpokenFormatting.Apply(input));

    [Fact]
    public void Does_not_format_twice_when_smart_already_added_a_break()
    {
        Assert.Equal("Intro.\n\nNext.", SpokenFormatting.Apply("Intro.\n\nNew paragraph.\n\nNext."));
    }
}

public class ThoughtBufferTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static CommittedSegment Seg(int i, string text, double seconds) => new(i, text, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Releases_a_chunk_at_a_complete_sentence_after_enough_speech()
    {
        var b = new ThoughtBuffer();
        Assert.Null(b.Add(Seg(0, "So I was thinking about the parser.", 3), T0)); // complete, but only 3 s
        var chunk = b.Add(Seg(1, "It should handle comments too.", 3), T0.AddSeconds(4));
        Assert.NotNull(chunk);
        Assert.Equal("So I was thinking about the parser. It should handle comments too.", chunk.Text);
        Assert.Equal((0, 1), (chunk.FirstSegment, chunk.LastSegment));
        Assert.True(b.IsEmpty);
    }

    [Fact]
    public void A_pause_does_not_end_a_thought_that_trails_off()
    {
        var b = new ThoughtBuffer();
        Assert.Null(b.Add(Seg(0, "I want to refactor the parser and", 8), T0));
        Assert.Null(b.Tick(T0.AddSeconds(3))); // idle, but trailing "and"
        var chunk = b.Add(Seg(1, "the tokenizer.", 3), T0.AddSeconds(4));
        Assert.Equal("I want to refactor the parser and the tokenizer.", chunk!.Text);
    }

    [Fact]
    public void Long_unfinished_speech_is_released_at_the_hard_limit()
    {
        var b = new ThoughtBuffer();
        Assert.Null(b.Add(Seg(0, "so basically the thing is that,", 14), T0));
        Assert.Null(b.Add(Seg(1, "and then we also have the other thing,", 10), T0));
        Assert.NotNull(b.Add(Seg(2, "which, and", 7), T0)); // 31 s ≥ 30 s hard max
    }

    [Fact]
    public void Idle_time_releases_short_complete_or_any_long_waiting_text()
    {
        var b = new ThoughtBuffer();
        b.Add(Seg(0, "Quick question.", 1.5), T0);
        Assert.Null(b.Tick(T0.AddSeconds(1)));
        Assert.Equal("Quick question.", b.Tick(T0.AddSeconds(3))!.Text);

        b.Add(Seg(1, "hmm, so maybe", 1.5), T0.AddSeconds(10));
        Assert.Null(b.Tick(T0.AddSeconds(13)));
        Assert.NotNull(b.Tick(T0.AddSeconds(19))); // long idle: don't wait indefinitely
    }

    [Fact]
    public void Final_flush_releases_everything_and_is_marked_final()
    {
        var b = new ThoughtBuffer();
        b.Add(Seg(0, "unfinished and", 1), T0);
        var chunk = b.Flush(isFinal: true);
        Assert.True(chunk!.IsFinal);
        Assert.Null(b.Flush(isFinal: true));
    }

    [Fact]
    public void Empty_segments_are_ignored_and_sequences_increase()
    {
        var b = new ThoughtBuffer();
        Assert.Null(b.Add(Seg(0, "   ", 6), T0));
        Assert.True(b.IsEmpty);
        Assert.Equal(0, b.Add(Seg(1, "One.", 6), T0)!.Sequence);
        Assert.Equal(1, b.Add(Seg(2, "Two.", 6), T0)!.Sequence);
        Assert.Null(b.Flush(true));
    }

    [Theory]
    [InlineData("That's it.", true)]
    [InlineData("Is it?", true)]
    [InlineData("I think, and", false)]
    [InlineData("first,", false)]
    [InlineData("so...", false)]
    [InlineData("no punctuation", false)]
    public void Detects_complete_sentences(string text, bool complete) => Assert.Equal(complete, ThoughtBuffer.IsComplete(text));
}

public class ProgressivePromptTests
{
    [Fact]
    public void Customized_prompt_is_preserved_and_managed_rules_are_appended()
    {
        const string custom = "My own editing rules.\nKeep my swearing.";
        var system = CleanupPromptBuilder.BuildSystemInstruction(custom, 35, [], progressive: true);
        Assert.StartsWith(custom, system);
        Assert.Contains("Formatting Rules", system);
        Assert.Contains("Progressive Dictation Rules", system);
        Assert.Contains("Treat previously committed text as read-only context", system);
        Assert.Contains("return an empty response", system);
    }

    [Fact]
    public void Whole_dictation_gets_formatting_rules_but_not_progressive_rules()
    {
        var system = CleanupPromptBuilder.BuildSystemInstruction(null, 35, [], progressive: false);
        Assert.Contains("intent-aware transcription refinement engine", system);
        Assert.Contains("Formatting Rules", system);
        Assert.DoesNotContain("Progressive Dictation Rules", system);
    }

    [Fact]
    public void Saved_custom_prompt_never_contains_the_managed_rules()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambler-prompt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new SettingsService(Path.Combine(dir, "settings.json"));
            var s = service.Current;
            s.CleanupPrompt = "Custom prompt text.";
            service.Save(s);
            var reloaded = new SettingsService(Path.Combine(dir, "settings.json")).Current;
            Assert.Equal("Custom prompt text.", reloaded.CleanupPrompt);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Progressive_request_separates_read_only_context_from_the_chunk()
    {
        var request = new CleanupRequest("and then the second part", "First part was inserted.", Progressive: true, IsFinal: false);
        var json = CleanupPromptBuilder.BuildRequestJson(request, new CleanupOptions("m", null, 35, "", []));
        using var doc = JsonDocument.Parse(json);
        var user = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Equal(
            "<committed_context>\nFirst part was inserted.\n</committed_context>\n<transcript final=\"false\">\nand then the second part\n</transcript>",
            user);
        var system = doc.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("First part was inserted.", system);
    }

    [Theory]
    [InlineData("\n\nNew topic here.", "Earlier text.", "\n\nNew topic here.")]
    [InlineData("  continuing the thought.", "Earlier text.", "continuing the thought.")]
    [InlineData("<committed_context>Earlier</committed_context>New bit.", "Earlier", "New bit.")]
    [InlineData("I think we should ship it. And test more.", "Long context. I think we should ship it.", "And test more.")]
    [InlineData("   ", "x", "")]
    [InlineData("<transcript final=\"true\">\nDone.\n</transcript>", "", "Done.")]
    public void Chunk_output_is_sanitized(string raw, string context, string expected) =>
        Assert.Equal(expected, CleanupOutputSanitizer.SanitizeChunk(raw, context));

    [Fact]
    public async Task Progressive_cleanup_may_return_empty_without_error()
    {
        var handler = new FakeHttpHandler().Respond(System.Net.HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[]},"finishReason":"STOP"}]}""");
        var service = new GeminiCleanupService(new GeminiHttp(new HttpClient(handler)));
        var result = await service.CleanAsync(new CleanupRequest("um", "ctx", Progressive: true, IsFinal: false),
            new CleanupOptions("m", null, 35, "", []), "AIzaTESTKEY0000000000000000000000000000", default);
        Assert.Equal("", result);
    }
}

public class ProgressiveOutputTests
{
    private static readonly CleanupOptions s_cleanupOptions = new("m", null, 35, "", []);
    private static CommittedSegment Seg(int i, string text, double seconds = 3) => new(i, text, TimeSpan.FromSeconds(seconds));

    private static ProgressiveOutputOptions Fast => new()
    {
        TickInterval = TimeSpan.Zero,
        TargetPollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static ProgressiveOutput Create(DictationMode mode, FakeInserter inserter, FakeCleanup? cleanup = null,
        InsertionTarget? target = null, ProgressiveOutputOptions? options = null) =>
        new(mode, target ?? inserter.Target, inserter, cleanup ?? new FakeCleanup(), s_cleanupOptions, "key", options ?? Fast);

    [Fact]
    public async Task Smart_only_inserts_each_committed_segment_promptly_in_order_without_cleanup()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup();
        await using var output = Create(DictationMode.SmartOnly, inserter, cleanup);

        output.Add(Seg(0, "Hello there."));
        await Wait.Until(() => inserter.Inserted.Count == 1);
        output.Add(Seg(1, "Second sentence."));
        await Wait.Until(() => inserter.Inserted.Count == 2);
        var result = await output.CompleteAsync(false, default);

        Assert.Equal(["Hello there.", " Second sentence."], inserter.Inserted.Select(i => i.Text));
        Assert.Empty(cleanup.Inputs);
        Assert.Equal("Hello there. Second sentence.", result.InsertedText);
        Assert.Equal("", result.PendingText);
        Assert.All(inserter.Options, o => Assert.False(o.AllowRefocus)); // never steals focus while listening
    }

    [Fact]
    public async Task Smart_only_turns_spoken_new_paragraph_into_a_real_break()
    {
        var inserter = new FakeInserter();
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "First topic."));
        output.Add(Seg(1, "New paragraph."));
        output.Add(Seg(2, "Second topic."));
        var result = await output.CompleteAsync(false, default);
        Assert.Equal("First topic.\n\nSecond topic.", result.InsertedText);
    }

    [Fact]
    public async Task Prompt_cleanup_chunks_thoughts_and_sends_committed_text_as_read_only_context()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup { Transform = t => t.ToUpperInvariant() };
        await using var output = Create(DictationMode.PromptCleanup, inserter, cleanup);

        output.Add(Seg(0, "first thought is done.", 6)); // complete + ≥ 5 s → chunk
        await Wait.Until(() => inserter.Inserted.Count == 1);
        output.Add(Seg(1, "second thought and", 3)); // trails off → buffered
        var result = await output.CompleteAsync(false, default); // flushed as the final chunk

        Assert.Equal(2, cleanup.Requests.Count);
        Assert.All(cleanup.Requests, r => Assert.True(r.Progressive));
        Assert.Equal("", cleanup.Requests[0].CommittedContext);
        Assert.False(cleanup.Requests[0].IsFinal);
        Assert.Equal("FIRST THOUGHT IS DONE.", cleanup.Requests[1].CommittedContext); // read-only context = committed output
        Assert.True(cleanup.Requests[1].IsFinal);
        Assert.Equal("second thought and", cleanup.Requests[1].Transcript); // only new speech is sent for cleaning
        Assert.Equal("FIRST THOUGHT IS DONE. SECOND THOUGHT AND", result.InsertedText);
    }

    [Fact]
    public async Task Slow_cleanup_responses_never_land_after_later_chunks()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup
        {
            Transform = t => t,
            Delay = r => r.Transcript.StartsWith("One") ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero,
        };
        await using var output = Create(DictationMode.PromptCleanup, inserter, cleanup);
        output.Add(Seg(0, "One is slow.", 6));
        output.Add(Seg(1, "Two is fast.", 6));
        output.Add(Seg(2, "Three is fast.", 6));
        var result = await output.CompleteAsync(false, default);

        Assert.Equal("One is slow. Two is fast. Three is fast.", result.InsertedText);
        Assert.Equal(1, inserter.MaxConcurrentInsertions);
    }

    [Fact]
    public async Task Every_committed_segment_is_inserted_exactly_once()
    {
        var inserter = new FakeInserter();
        await using var output = Create(DictationMode.SmartOnly, inserter);
        for (var i = 0; i < 20; i++) output.Add(Seg(i, $"S{i}."));
        var result = await output.CompleteAsync(false, default);

        var expected = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"S{i}."));
        Assert.Equal(expected, inserter.AllInsertedText);
        Assert.Equal(expected, result.AllText);
    }

    [Fact]
    public async Task Insertion_pauses_while_the_target_is_not_focused_and_resumes_when_it_returns()
    {
        var inserter = new FakeInserter { Ready = false };
        await using var output = Create(DictationMode.SmartOnly, inserter);

        output.Add(Seg(0, "Waiting text."));
        await Wait.Until(() => output.Status == OutputStatus.WaitingForTarget);
        await Task.Delay(100);
        Assert.Empty(inserter.Inserted); // never types into whatever else is focused

        inserter.SetFocus(true);
        await Wait.Until(() => inserter.Inserted.Count == 1);
        Assert.Equal("Waiting text.", inserter.Inserted[0].Text);
    }

    [Fact]
    public async Task At_stop_text_for_an_unfocused_target_is_kept_pending()
    {
        var inserter = new FakeInserter();
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "Typed."));
        await Wait.Until(() => inserter.Inserted.Count == 1);

        inserter.SetFocus(false); // the user switched apps
        output.Add(Seg(1, "Kept for later."));
        var result = await output.CompleteAsync(allowRefocus: false, default);

        Assert.Equal("Typed.", result.InsertedText);
        Assert.Equal(" Kept for later.", result.PendingText);
        Assert.Single(inserter.Inserted);
    }

    [Fact]
    public async Task Stopping_from_the_popup_allows_one_refocus_for_the_final_text()
    {
        var inserter = new FakeInserter { Ready = false };
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "Final words."));
        var result = await output.CompleteAsync(allowRefocus: true, default);

        Assert.Equal("Final words.", result.InsertedText);
        Assert.Contains(inserter.Options, o => o.AllowRefocus);
    }

    [Fact]
    public async Task Partially_typed_text_resumes_without_duplication()
    {
        var calls = 0;
        var inserter = new FakeInserter();
        inserter.Result = text => ++calls == 1
            ? new InsertionResult(InsertionOutcome.TargetNotReady, "focus lost", CharsInserted: 6)
            : InsertionResult.Success;
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "Hello world, again."));
        var result = await output.CompleteAsync(false, default);

        Assert.Equal("Hello world, again.", inserter.AllInsertedText);
        Assert.Equal("Hello world, again.", result.InsertedText);
    }

    [Fact]
    public async Task Destroyed_target_halts_insertion_and_keeps_everything_after_it_in_order()
    {
        var inserter = new FakeInserter();
        inserter.Result = text => text.Contains("Two")
            ? new InsertionResult(InsertionOutcome.Failed, "The window you were dictating into is gone.")
            : InsertionResult.Success;
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "One."));
        output.Add(Seg(1, "Two."));
        output.Add(Seg(2, "Three."));
        var result = await output.CompleteAsync(false, default);

        Assert.Equal("One.", result.InsertedText);
        Assert.Equal(" Two. Three.", result.PendingText);
        Assert.Contains("gone", result.Problem);
        Assert.Equal(OutputStatus.Halted, output.Status);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_the_smart_transcript_and_never_loses_text()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup
        {
            ThrowFor = r => r.Transcript.StartsWith("Broken") ? new GeminiException(GeminiErrorKind.Server, "boom") : null,
            Transform = t => t,
        };
        await using var output = Create(DictationMode.PromptCleanup, inserter, cleanup);
        output.Add(Seg(0, "Good chunk here.", 6));
        output.Add(Seg(1, "Broken chunk here.", 6));
        output.Add(Seg(2, "Later chunk here.", 6));
        var result = await output.CompleteAsync(false, default);

        Assert.Equal("Good chunk here.", result.InsertedText);
        Assert.Equal(" Broken chunk here. Later chunk here.", result.PendingText); // raw SMART text kept, order kept
        Assert.Contains("Cleanup failed", result.Problem);
    }

    [Fact]
    public async Task Empty_cleanup_output_inserts_nothing()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup { Transform = t => t == "Just filler here." ? "" : t };
        await using var output = Create(DictationMode.PromptCleanup, inserter, cleanup);
        output.Add(Seg(0, "Real content here.", 6));
        output.Add(Seg(1, "Just filler here.", 6));
        output.Add(Seg(2, "More content.", 6));
        var result = await output.CompleteAsync(false, default);
        Assert.Equal("Real content here. More content.", result.InsertedText);
    }

    [Fact]
    public async Task Non_editable_target_never_receives_text()
    {
        var inserter = new FakeInserter();
        var desktop = new InsertionTarget(5, 9, "Desktop") { Kind = TargetKind.NotEditable, AppName = "Desktop" };
        await using var output = Create(DictationMode.SmartOnly, inserter, target: desktop);
        output.Add(Seg(0, "Nowhere to go."));
        var result = await output.CompleteAsync(allowRefocus: true, default);
        Assert.Empty(inserter.Inserted);
        Assert.Equal("Nowhere to go.", result.PendingText);
    }

    [Fact]
    public async Task Segments_arriving_after_completion_are_ignored()
    {
        var inserter = new FakeInserter();
        await using var output = Create(DictationMode.SmartOnly, inserter);
        output.Add(Seg(0, "In time."));
        await output.CompleteAsync(false, default);
        output.Add(Seg(1, "Too late."));
        await Task.Delay(50);
        Assert.Equal("In time.", inserter.AllInsertedText);
    }

    [Fact]
    public async Task Switching_to_smart_only_flushes_buffered_cleanup_text_first()
    {
        var inserter = new FakeInserter();
        var cleanup = new FakeCleanup { Transform = t => "[" + t + "]" };
        await using var output = Create(DictationMode.PromptCleanup, inserter, cleanup);
        output.Add(Seg(0, "buffered and", 2));
        output.SetMode(DictationMode.SmartOnly);
        output.Add(Seg(1, "Direct."));
        var result = await output.CompleteAsync(false, default);
        Assert.Equal("[buffered and] Direct.", result.InsertedText);
    }
}

public class FollowFocusTests
{
    private static readonly CleanupOptions s_cleanupOptions = new("m", null, 35, "", []);
    private static CommittedSegment Seg(int i, string text) => new(i, text, TimeSpan.FromSeconds(3));
    private static readonly InsertionTarget Notepad = new(1, 10, "Notepad") { AppName = "Notepad", Kind = TargetKind.Editable };
    private static readonly InsertionTarget Discord = new(2, 20, "Discord") { AppName = "Discord", Kind = TargetKind.Editable };
    private static readonly InsertionTarget Desktop = new(3, 30, "Desktop") { AppName = "Desktop", Kind = TargetKind.NotEditable };

    private static ProgressiveOutput Create(FakeInserter inserter, InsertionTarget start) =>
        new(DictationMode.SmartOnly, start, inserter, new FakeCleanup(), s_cleanupOptions, "key",
            new ProgressiveOutputOptions { TickInterval = TimeSpan.Zero, TargetPollInterval = TimeSpan.FromMilliseconds(20), FollowFocus = true });

    [Fact]
    public async Task Text_follows_the_user_into_another_app()
    {
        var inserter = new FakeInserter { Target = Notepad };
        await using var output = Create(inserter, Notepad);
        output.Add(Seg(0, "In Notepad."));
        await Wait.Until(() => inserter.Inserted.Count == 1);

        inserter.Target = Discord; // the user switched apps
        output.Add(Seg(1, "In Discord."));
        await Wait.Until(() => inserter.Inserted.Count == 2);
        await output.CompleteAsync(false, default);

        Assert.Equal(Notepad, inserter.Inserted[0].Target);
        Assert.Equal(Discord, inserter.Inserted[1].Target);
        Assert.Equal(Discord, output.CurrentTarget);
    }

    [Fact]
    public async Task Starting_without_a_text_field_waits_until_one_is_focused()
    {
        var inserter = new FakeInserter { Target = Desktop };
        await using var output = Create(inserter, Desktop);
        output.Add(Seg(0, "Hold this."));
        await Wait.Until(() => output.Status == OutputStatus.WaitingForTarget);
        Assert.Empty(inserter.Inserted);

        inserter.Target = Notepad;
        inserter.SetFocus(true);
        await Wait.Until(() => inserter.Inserted.Count == 1);
        Assert.Equal(Notepad, inserter.Inserted[0].Target);
    }

    [Fact]
    public async Task An_app_that_refuses_text_pauses_insertion_instead_of_stopping_it()
    {
        var admin = new InsertionTarget(9, 90, "Admin terminal") { AppName = "Terminal", Kind = TargetKind.Editable };
        var inserter = new FakeInserter { Target = admin };
        inserter.Result = _ => inserter.Target == admin
            ? new InsertionResult(InsertionOutcome.Failed, "runs as administrator")
            : InsertionResult.Success;
        await using var output = Create(inserter, admin);
        output.Add(Seg(0, "Waits for a normal app."));
        await Wait.Until(() => output.Status == OutputStatus.WaitingForTarget);

        inserter.Target = Notepad;
        inserter.SetFocus(true);
        await Wait.Until(() => inserter.Inserted.Count == 1);
        var result = await output.CompleteAsync(false, default);
        Assert.Equal("Waits for a normal app.", result.InsertedText);
        Assert.Null(result.Problem);
    }

    [Fact]
    public async Task Rambler_settings_default_to_following_focus()
    {
        Assert.True(new AppSettings().FollowFocus);
        var audio = new FakeAudioSource();
        var sessions = new FakeSessionFactory { Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false } };
        var inserter = new FakeInserter { Target = Notepad };
        var c = new DictationCoordinator(audio, sessions, new FakeCleanup(), inserter,
            () => new AppSettings { DefaultMode = DictationMode.SmartOnly }, () => "AIzaTESTKEY0000000000000000000000000000");
        c.Start(DictationTrigger.Default);
        inserter.Target = Discord;
        sessions.Sessions[0].RaiseCommitted(0, "Goes to Discord.");
        await Wait.Until(() => inserter.Inserted.Count == 1);
        Assert.Equal(Discord, inserter.Inserted[0].Target);
        Assert.Equal("Discord", c.Target.DisplayName);
        await c.StopAsync();
    }

    [Fact]
    public async Task Insert_when_finished_goes_where_the_user_is_at_stop()
    {
        var inserter = new FakeInserter { Target = Notepad };
        var c = new DictationCoordinator(new FakeAudioSource(), new FakeSessionFactory(), new FakeCleanup(), inserter,
            () => new AppSettings { DefaultMode = DictationMode.SmartOnly, InsertWhenFinished = true },
            () => "AIzaTESTKEY0000000000000000000000000000");
        c.Start(DictationTrigger.Default);
        inserter.Target = Discord;
        await c.StopAsync();
        Assert.Equal(Discord, Assert.Single(inserter.Inserted).Target);
    }

    [Fact]
    public async Task Fixed_target_mode_still_never_follows_focus()
    {
        var inserter = new FakeInserter { Target = Notepad };
        await using var output = new ProgressiveOutput(DictationMode.SmartOnly, Notepad, inserter, new FakeCleanup(), s_cleanupOptions, "key",
            new ProgressiveOutputOptions { TickInterval = TimeSpan.Zero, TargetPollInterval = TimeSpan.FromMilliseconds(20), FollowFocus = false });
        inserter.Target = Discord;
        output.Add(Seg(0, "Only Notepad."));
        await Wait.Until(() => inserter.Inserted.Count == 1);
        Assert.Equal(Notepad, inserter.Inserted[0].Target);
    }
}

public class EndpointingTests
{
    private const string Key = "AIzaTESTKEY0000000000000000000000000000";

    private static TranscriptionOptions Options() =>
        new(new LiveSetupOptions("gemini-3.5-transcribe-live", true, null, []),
            new RecordedTranscriptionOptions("gemini-3.5-transcribe", true, null, []), true)
        {
            SetupTimeout = TimeSpan.FromSeconds(2),
            FinalizeTimeout = TimeSpan.FromMilliseconds(500),
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
            Endpointing = new PauseEndpointing(),
        };

    private static void Feed(ITranscriptionSession session, double seconds, bool speech)
    {
        var total = (int)(seconds * 32000);
        for (var offset = 0; offset < total; offset += 3200)
        {
            var chunk = new byte[Math.Min(3200, total - offset)];
            if (speech) for (var i = 0; i < chunk.Length; i++) chunk[i] = (byte)((i * 37) % 251);
            session.WriteAudio(chunk);
        }
    }

    [Fact]
    public async Task A_natural_pause_commits_the_utterance_while_still_listening()
    {
        var factory = new FakeWebSocketFactory(i => LiveServer.Transcribing($"Part {i}."));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        var committed = new List<CommittedSegment>();
        session.Committed += c => { lock (committed) committed.Add(c); };
        session.Start();

        Feed(session, 3, speech: true);
        Feed(session, 1.5, speech: false); // natural pause → utterance ends
        await Wait.Until(() => { lock (committed) return committed.Count == 1; });
        Assert.Equal("Part 0.", committed[0].Text); // available before the user stops

        Feed(session, 3, speech: true);
        var result = await session.CompleteAsync(default);

        Assert.Equal(["Part 0.", "Part 1."], committed.Select(c => c.Text));
        Assert.Equal([0, 1], committed.Select(c => c.Index));
        Assert.Equal("Part 0. Part 1.", result.Text);
    }

    [Fact]
    public async Task Short_thinking_pauses_do_not_split_the_utterance()
    {
        var factory = new FakeWebSocketFactory(i => LiveServer.Transcribing($"Part {i}."));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        session.Start();
        Feed(session, 3, speech: true);
        Feed(session, 0.7, speech: false);
        Feed(session, 2, speech: true);
        Feed(session, 0.5, speech: false);
        Feed(session, 2, speech: true);
        var result = await session.CompleteAsync(default);

        Assert.Single(factory.Created);
        Assert.Equal("Part 0.", result.Text);
    }

    [Fact]
    public async Task Silence_before_any_speech_never_splits()
    {
        var factory = new FakeWebSocketFactory(i => LiveServer.Transcribing($"Part {i}."));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        session.Start();
        Feed(session, 5, speech: false);
        Feed(session, 3, speech: true);
        await session.CompleteAsync(default);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task A_failed_utterance_is_recovered_by_fallback_and_committed_in_order()
    {
        var factory = new FakeWebSocketFactory(index =>
        {
            if (index > 0) return LiveServer.Transcribing("Second.");
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                switch (LiveServer.MessageType(msg))
                {
                    case "setup": s.ServerSend(LiveServer.SetupComplete); break;
                    case "audio": s.ServerClose(System.Net.WebSockets.WebSocketCloseStatus.InternalServerError, "Internal error"); break;
                }
                return Task.CompletedTask;
            };
            return ws;
        });
        var recorded = new FakeRecordedTranscriber { Result = _ => "First." };
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);
        var committed = new List<string>();
        session.Committed += c => { lock (committed) committed.Add(c.Text); };
        session.Start();

        Feed(session, 1, speech: true);
        await Wait.Until(() => factory.Created.Count == 2);
        Feed(session, 3, speech: true);
        await session.CompleteAsync(default);

        Assert.Equal(["First.", "Second."], committed);
    }

    [Fact]
    public async Task Each_utterance_is_committed_exactly_once_even_with_cumulative_finals()
    {
        var factory = new FakeWebSocketFactory(i => LiveServer.Transcribing($"Utterance {i}", $"Utterance {i} done."));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        var committed = new List<string>();
        session.Committed += c => { lock (committed) committed.Add(c.Text); };
        session.Start();
        for (var i = 0; i < 3; i++)
        {
            Feed(session, 3, speech: true);
            Feed(session, 1.5, speech: false);
        }
        await session.CompleteAsync(default);
        Assert.Equal(["Utterance 0 done.", "Utterance 1 done.", "Utterance 2 done."], committed);
    }
}

public class ProgressiveCoordinatorTests
{
    private readonly FakeAudioSource _audio = new();
    private readonly FakeSessionFactory _sessions = new();
    private readonly FakeCleanup _cleanup = new();
    private readonly FakeInserter _inserter = new();
    private readonly AppSettings _settings = new();

    private DictationCoordinator Create() =>
        new(_audio, _sessions, _cleanup, _inserter, () => _settings.Clone(), () => "AIzaTESTKEY0000000000000000000000000000");

    [Fact]
    public async Task Smart_only_text_appears_before_the_user_stops()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        _sessions.Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false };
        var c = Create();
        c.Start(DictationTrigger.Default);
        var session = _sessions.Sessions[0];

        session.RaiseCommitted(0, "First sentence.");
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        Assert.Equal(DictationState.Listening, c.State); // still listening
        Assert.Empty(_cleanup.Inputs);

        session.RaiseCommitted(1, "Second sentence.");
        session.Result = new TranscriptionResult("First sentence. Second sentence.", [], false);
        await c.StopAsync();

        Assert.Equal("First sentence. Second sentence.", _inserter.AllInsertedText);
        Assert.Equal(DictationState.Idle, c.State);
        Assert.True(c.LastCompleted);
        Assert.Equal("First sentence. Second sentence.", c.LastResult);
    }

    [Fact]
    public async Task Prompt_cleanup_inserts_cleaned_chunks_progressively()
    {
        _sessions.Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false };
        var c = Create();
        c.Start(DictationTrigger.Default);
        _sessions.Sessions[0].RaiseCommitted(0, "this is a whole thought.", 6);
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        Assert.Equal("CLEAN: this is a whole thought.", _inserter.Inserted[0].Text);
        Assert.Equal(DictationState.Listening, c.State);
        await c.StopAsync();
    }

    [Fact]
    public async Task Bypass_hotkey_is_smart_only_and_progressive()
    {
        _sessions.Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false };
        var c = Create();
        c.Start(DictationTrigger.SmartBypass);
        _sessions.Sessions[0].RaiseCommitted(0, "Raw words.");
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        await c.StopAsync();
        Assert.Empty(_cleanup.Inputs);
        Assert.Equal(DictationMode.PromptCleanup, _settings.DefaultMode);
    }

    [Fact]
    public async Task Text_left_for_an_unfocused_target_is_offered_and_inserted_where_the_user_is()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();
        c.Start(DictationTrigger.Default);
        _inserter.Ready = false; // the user moved to another app before stopping
        await c.StopAsync();

        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal("hello world", c.PendingText);
        Assert.Contains("wasn't focused", c.Message);
        Assert.Empty(_inserter.Inserted);

        var discord = new InsertionTarget(77, 8, "Discord") { AppName = "Discord", Kind = TargetKind.Editable };
        _inserter.Target = discord; // where the user is now
        await c.InsertPendingAsync();
        var inserted = Assert.Single(_inserter.Inserted);
        Assert.Equal(discord, inserted.Target);
        Assert.True(_inserter.Options[^1].AllowRefocus); // explicit user action
    }

    [Fact]
    public async Task Late_events_from_a_cancelled_dictation_never_reach_the_next_one()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        _sessions.Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false };
        var c = Create();
        c.Start(DictationTrigger.Default);
        var old = _sessions.Sessions[0];
        await c.CancelAsync();

        c.Start(DictationTrigger.Default);
        var current = _sessions.Sessions[1];
        old.RaiseCommitted(5, "Stale text.");
        current.RaiseCommitted(0, "Fresh text.");
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        await Task.Delay(100);

        Assert.Equal("Fresh text.", _inserter.AllInsertedText);
        Assert.NotEqual(1, c.SessionId);
        await c.CancelAsync();
    }

    [Fact]
    public async Task Stop_then_immediate_restart_does_not_mix_outputs()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        _sessions.Next = () => new FakeTranscriptionSession { EmitResultOnComplete = false };
        var c = Create();
        c.Start(DictationTrigger.Default);
        _sessions.Sessions[0].RaiseCommitted(0, "One.");
        await c.StopAsync();
        Assert.True(c.Start(DictationTrigger.Default));
        _sessions.Sessions[1].RaiseCommitted(0, "Two.");
        await Wait.Until(() => _inserter.Inserted.Count == 2);
        _sessions.Sessions[0].RaiseCommitted(1, "Ghost.");
        await c.StopAsync();
        Assert.Equal(["One.", "Two."], _inserter.Inserted.Select(i => i.Text));
    }

    [Fact]
    public async Task Cleanup_failure_offers_the_smart_transcript()
    {
        _cleanup.Throw = new GeminiException(GeminiErrorKind.Server, "boom");
        var c = Create();
        c.Start(DictationTrigger.Default);
        await c.StopAsync();
        Assert.Equal(DictationState.Error, c.State);
        Assert.Equal("hello world", c.PendingText);
        Assert.Contains("Cleanup failed", c.Message);
        Assert.Empty(_inserter.Inserted);
    }

    [Fact]
    public async Task Insert_when_finished_buffers_until_stop()
    {
        _settings.InsertWhenFinished = true;
        _settings.DefaultMode = DictationMode.SmartOnly;
        var c = Create();
        c.Start(DictationTrigger.Default);
        Assert.Null(_sessions.LastOptions!.Endpointing); // one utterance for whole-session coherence
        await c.StopAsync();
        Assert.Equal("hello world", Assert.Single(_inserter.Inserted).Text);
    }

    [Fact]
    public void Progressive_mode_enables_pause_endpointing()
    {
        var c = Create();
        c.Start(DictationTrigger.Default);
        Assert.NotNull(_sessions.LastOptions!.Endpointing);
    }

    [Fact]
    public async Task Fatal_error_keeps_already_inserted_text_and_reports_it()
    {
        _settings.DefaultMode = DictationMode.SmartOnly;
        var session = new FakeTranscriptionSession { EmitResultOnComplete = false };
        _sessions.Next = () => session;
        var c = Create();
        c.Start(DictationTrigger.Default);
        session.RaiseCommitted(0, "Before the error.");
        await Wait.Until(() => _inserter.Inserted.Count == 1);
        session.RaiseFatal(new GeminiException(GeminiErrorKind.QuotaExceeded, "quota"));
        await Wait.Until(() => c.State == DictationState.Error);
        Assert.False(_audio.IsCapturing);
        Assert.Equal("Before the error.", _inserter.AllInsertedText);
    }
}
