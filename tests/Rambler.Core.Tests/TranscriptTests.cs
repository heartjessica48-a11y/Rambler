using System.Text.Json;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

public class TranscriptAccumulatorTests
{
    [Fact]
    public void Interim_results_replace_each_other_and_are_not_committed()
    {
        var acc = new TranscriptAccumulator();
        acc.OnInterim("so I");
        acc.OnInterim("so I think we");
        Assert.Equal("so I think we", acc.DisplayText);
        Assert.Equal(string.Empty, acc.FinalText);
        Assert.False(acc.HasFinalText);
    }

    [Fact]
    public void Finals_are_committed_and_clear_the_interim()
    {
        var acc = new TranscriptAccumulator();
        acc.OnInterim("hello wor");
        acc.OnFinal("Hello world.");
        Assert.Equal("Hello world.", acc.FinalText);
        Assert.Equal("Hello world.", acc.DisplayText);
    }

    [Fact]
    public void Delta_finals_are_appended()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("First thought.");
        acc.OnFinal("Second thought.");
        Assert.Equal("First thought. Second thought.", acc.FinalText);
    }

    [Fact]
    public void Cumulative_finals_do_not_duplicate()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("I want to");
        acc.OnFinal("I want to refactor the parser");
        acc.OnFinal("I want to refactor the parser, not rewrite it.");
        Assert.Equal("I want to refactor the parser, not rewrite it.", acc.FinalText);
    }

    [Fact]
    public void Repeated_final_delivery_is_ignored()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("Same text.");
        acc.OnFinal("Same text.");
        Assert.Equal("Same text.", acc.FinalText);
    }

    [Fact]
    public void Turns_are_sealed_and_later_finals_start_fresh()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("Turn one.");
        acc.OnTurnComplete();
        acc.OnFinal("Turn one."); // same words in a new turn are legitimate, not a duplicate of the sealed turn
        Assert.Equal("Turn one. Turn one.", acc.FinalText);
    }

    [Fact]
    public void Interim_never_rewrites_committed_text()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("Committed part.");
        acc.OnTurnComplete();
        acc.OnInterim("something totally different");
        Assert.Equal("Committed part.", acc.FinalText);
        Assert.Equal("Committed part. something totally different", acc.DisplayText);
    }

    [Fact]
    public void Cumulative_interim_covering_turn_is_not_shown_twice()
    {
        var acc = new TranscriptAccumulator();
        acc.OnFinal("Let me think");
        acc.OnInterim("Let me think about this again");
        Assert.Equal("Let me think about this again", acc.DisplayText);
    }

    [Fact]
    public void Best_effort_uses_interim_only_when_no_finals_exist()
    {
        var acc = new TranscriptAccumulator();
        acc.OnInterim("unfinalized words");
        Assert.Equal("unfinalized words", acc.BestEffortText);
        acc.OnFinal("Final words.");
        acc.OnInterim("trailing guess");
        Assert.Equal("Final words.", acc.BestEffortText);
    }

    [Theory]
    [InlineData(new[] { "a", "b" }, "a b")]
    [InlineData(new[] { "Hello", ", world" }, "Hello, world")]
    [InlineData(new[] { "Para one.\n\n", "Para two." }, "Para one.\n\nPara two.")]
    [InlineData(new[] { "", "x", "" }, "x")]
    public void Joiner_spaces_segments_sensibly(string[] parts, string expected) =>
        Assert.Equal(expected, TranscriptJoiner.Join(parts));
}

public class LiveProtocolTests
{
    [Fact]
    public void Setup_uses_documented_fields_smart_mode_and_manual_endpointing()
    {
        var json = LiveProtocol.BuildSetup(new LiveSetupOptions("gemini-3.5-transcribe-live", true, null, ["Kubernetes", "BigQuery"]));
        using var doc = JsonDocument.Parse(json);
        var setup = doc.RootElement.GetProperty("setup");

        Assert.Equal("models/gemini-3.5-transcribe-live", setup.GetProperty("model").GetString());
        Assert.Equal("TEXT", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.True(setup.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection").GetProperty("disabled").GetBoolean());

        var t = setup.GetProperty("inputAudioTranscription");
        Assert.Equal("SMART", t.GetProperty("mode").GetString());
        Assert.False(t.TryGetProperty("languageCodes", out _)); // auto-detect when unset
        Assert.Equal(["Kubernetes", "BigQuery"], t.GetProperty("customVocabulary").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Setup_includes_language_only_when_configured()
    {
        var json = LiveProtocol.BuildSetup(new LiveSetupOptions("m", true, "en-US", []));
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("setup").GetProperty("inputAudioTranscription");
        Assert.Equal("en-US", t.GetProperty("languageCodes")[0].GetString());
        Assert.False(t.TryGetProperty("customVocabulary", out _));
    }

    [Fact]
    public void Audio_message_is_base64_pcm_at_16k()
    {
        byte[] pcm = [1, 2, 3, 250, 0, 128];
        using var doc = JsonDocument.Parse(LiveProtocol.BuildAudio(pcm));
        var audio = doc.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(pcm, Convert.FromBase64String(audio.GetProperty("data").GetString()!));
    }

    [Fact]
    public void Activity_markers_are_valid_json()
    {
        using var start = JsonDocument.Parse(LiveProtocol.ActivityStart);
        using var end = JsonDocument.Parse(LiveProtocol.ActivityEnd);
        Assert.True(start.RootElement.GetProperty("realtimeInput").TryGetProperty("activityStart", out _));
        Assert.True(end.RootElement.GetProperty("realtimeInput").TryGetProperty("activityEnd", out _));
    }

    [Fact]
    public void Parses_all_parts_of_a_server_message_in_order()
    {
        var events = LiveProtocol.Parse("""
            {"serverContent":{"interimInputTranscription":{"text":"draft"},
                              "inputTranscription":{"text":"Final.","finished":true},
                              "turnComplete":true}}
            """);
        Assert.Equal([LiveEventKind.Interim, LiveEventKind.Final, LiveEventKind.TurnComplete], events.Select(e => e.Kind));
        Assert.Equal("draft", events[0].Text);
        Assert.True(events[1].Finished);
    }

    [Fact]
    public void Ignores_unknown_fields_for_forward_compatibility()
    {
        var events = LiveProtocol.Parse("""{"usageMetadata":{"totalTokenCount":5},"serverContent":{"newThing":{"x":1}},"futureField":true}""");
        Assert.Empty(events);
    }

    [Fact]
    public void Parses_setup_complete_and_go_away()
    {
        Assert.Equal(LiveEventKind.SetupComplete, Assert.Single(LiveProtocol.Parse("""{"setupComplete":{}}""")).Kind);
        var goAway = Assert.Single(LiveProtocol.Parse("""{"goAway":{"timeLeft":"5s"}}"""));
        Assert.Equal(LiveEventKind.GoAway, goAway.Kind);
        Assert.Equal("5s", goAway.Text);
    }
}
