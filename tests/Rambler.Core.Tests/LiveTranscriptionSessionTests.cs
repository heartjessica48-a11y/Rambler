using System.Net.WebSockets;
using Rambler.Core.Gemini;
using Rambler.Core.Tests.Fakes;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

public class LiveTranscriptionSessionTests
{
    private const string Key = "AIzaTESTKEY0000000000000000000000000000";
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    private static TranscriptionOptions Options(bool fallback = true) =>
        new(new LiveSetupOptions("gemini-3.5-transcribe-live", true, null, []),
            new RecordedTranscriptionOptions("gemini-3.5-transcribe", true, null, []),
            fallback)
        {
            SetupTimeout = TimeSpan.FromSeconds(2),
            FinalizeTimeout = TimeSpan.FromMilliseconds(500),
            ReconnectDelay = TimeSpan.FromMilliseconds(10),
        };

    private static byte[] Audio(int bytes) => Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray();

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_testTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Streams_audio_with_manual_endpointing_and_returns_the_final_transcript()
    {
        var factory = new FakeWebSocketFactory(_ => LiveServer.Transcribing("Hello world."));
        var recorded = new FakeRecordedTranscriber();
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);
        var previews = new List<string>();
        session.PreviewChanged += p => { lock (previews) previews.Add(p); };

        session.Start();
        session.WriteAudio(Audio(16000)); // 0.5 s, buffered until the connection is ready
        var result = await session.CompleteAsync(CancellationToken.None).WaitAsync(s_testTimeout);

        Assert.Equal("Hello world.", result.Text);
        Assert.Empty(result.Warnings);
        Assert.False(result.UsedRecordedFallback);
        Assert.Empty(recorded.Calls);

        var ws = Assert.Single(factory.Created);
        Assert.Equal(GeminiEndpoints.LiveWebSocket, ws.Uri);
        Assert.DoesNotContain("key=", ws.Uri!.Query);
        Assert.Equal(Key, ws.Headers[GeminiEndpoints.ApiKeyHeader]);

        var types = ws.Sent.Select(LiveServer.MessageType).ToList();
        Assert.Equal("setup", types[0]);
        Assert.Equal("activityStart", types[1]);
        Assert.Equal("activityEnd", types[^1]);
        Assert.All(types.Skip(2).SkipLast(1), t => Assert.Equal("audio", t));
        Assert.Equal(16000, ws.AudioBytesSent);
        Assert.Equal(5, types.Count(t => t == "audio")); // batched into 100 ms chunks
        lock (previews) Assert.Contains(previews, p => p.StartsWith("partial"));
    }

    [Fact]
    public async Task Cumulative_final_updates_are_not_duplicated()
    {
        var factory = new FakeWebSocketFactory(_ => LiveServer.Transcribing("I want to", "I want to go home."));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        session.Start();
        session.WriteAudio(Audio(9600));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);
        Assert.Equal("I want to go home.", result.Text);
    }

    [Fact]
    public async Task Unavailable_live_model_falls_back_to_recorded_audio()
    {
        var factory = new FakeWebSocketFactory(_ =>
        {
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                if (LiveServer.MessageType(msg) == "setup")
                    s.ServerClose(WebSocketCloseStatus.PolicyViolation, "models/gemini-x is not found for API version v1beta");
                return Task.CompletedTask;
            };
            return ws;
        });
        var recorded = new FakeRecordedTranscriber();
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);
        var notices = new List<string>();
        session.Notice += n => { lock (notices) notices.Add(n); };

        session.Start();
        session.WriteAudio(Audio(16000));
        await WaitUntil(() => { lock (notices) return notices.Count > 0; });
        session.WriteAudio(Audio(16000));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.True(result.UsedRecordedFallback);
        Assert.Contains("recorded transcript", result.Text);
        Assert.Single(factory.Created); // no pointless reconnect loop for a missing model
        Assert.Equal(32000, recorded.Calls.Sum(w => w.Length - 44)); // all audio, as WAV
        Assert.All(recorded.Calls, w => Assert.Equal("RIFF"u8.ToArray(), w[..4]));
    }

    [Fact]
    public async Task Invalid_key_is_fatal_and_skips_fallback()
    {
        var factory = new FakeWebSocketFactory(_ =>
        {
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                s.ServerClose(WebSocketCloseStatus.PolicyViolation, "API key not valid. Please pass a valid API key.");
                return Task.CompletedTask;
            };
            return ws;
        });
        var recorded = new FakeRecordedTranscriber();
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);
        GeminiException? fatal = null;
        session.Fatal += e => fatal = e;

        session.Start();
        session.WriteAudio(Audio(16000));
        await WaitUntil(() => fatal is not null);
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal(GeminiErrorKind.InvalidApiKey, fatal!.Kind);
        Assert.Empty(recorded.Calls);
        Assert.Equal(string.Empty, result.Text);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Dropped_connection_reconnects_and_fallback_recovers_the_lost_segment_in_order()
    {
        var factory = new FakeWebSocketFactory(index =>
        {
            if (index > 0) return LiveServer.Transcribing("Second part.");
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                switch (LiveServer.MessageType(msg))
                {
                    case "setup": s.ServerSend(LiveServer.SetupComplete); break;
                    case "audio": s.ServerClose(WebSocketCloseStatus.InternalServerError, "Internal error"); break;
                }
                return Task.CompletedTask;
            };
            return ws;
        });
        var recorded = new FakeRecordedTranscriber { Result = _ => "First part." };
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);

        session.Start();
        session.WriteAudio(Audio(16000));
        await WaitUntil(() => factory.Created.Count == 2);
        session.WriteAudio(Audio(16000));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal("First part. Second part.", result.Text);
        Assert.True(result.UsedRecordedFallback);
        Assert.Single(recorded.Calls);
        Assert.Equal(16000, factory.Created[1].AudioBytesSent);
    }

    [Fact]
    public async Task Without_fallback_a_dropped_segment_keeps_partial_text_and_warns()
    {
        var factory = new FakeWebSocketFactory(index =>
        {
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                switch (LiveServer.MessageType(msg))
                {
                    case "setup": s.ServerSend(LiveServer.SetupComplete); break;
                    case "audio":
                        s.ServerSend(LiveServer.Final("Kept words."));
                        s.ServerClose(WebSocketCloseStatus.InternalServerError, "Internal error");
                        break;
                }
                return Task.CompletedTask;
            };
            return ws;
        });
        await using var session = new LiveTranscriptionSession(Options(fallback: false), Key, factory, new FakeRecordedTranscriber());
        session.Start();
        session.WriteAudio(Audio(16000));
        await WaitUntil(() => factory.Created.Count >= 2);
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.StartsWith("Kept words.", result.Text);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Rolls_over_to_a_new_connection_before_the_session_limit()
    {
        var factory = new FakeWebSocketFactory(index => LiveServer.Transcribing(index == 0 ? "Part one." : "Part two."));
        var options = Options() with { RolloverAfter = TimeSpan.FromMilliseconds(200) };
        await using var session = new LiveTranscriptionSession(options, Key, factory, new FakeRecordedTranscriber());

        session.Start();
        session.WriteAudio(Audio(9600));
        await WaitUntil(() => factory.Created.Count == 2);
        session.WriteAudio(Audio(9600));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal("Part one. Part two.", result.Text);
        Assert.Equal(9600, factory.Created[0].AudioBytesSent);
        Assert.Equal(9600, factory.Created[1].AudioBytesSent);
    }

    [Fact]
    public async Task Go_away_triggers_a_graceful_rollover()
    {
        var factory = new FakeWebSocketFactory(index =>
        {
            var ws = LiveServer.Transcribing(index == 0 ? "Before." : "After.");
            if (index == 0)
            {
                var inner = ws.OnClientMessage!;
                var sentGoAway = false;
                ws.OnClientMessage = async (s, msg) =>
                {
                    await inner(s, msg);
                    if (LiveServer.MessageType(msg) == "audio" && !sentGoAway)
                    {
                        sentGoAway = true;
                        s.ServerSend("""{"goAway":{"timeLeft":"10s"}}""");
                    }
                };
            }
            return ws;
        });
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());

        session.Start();
        session.WriteAudio(Audio(3200));
        await WaitUntil(() => factory.Created.Count == 2);
        session.WriteAudio(Audio(3200));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal("Before. After.", result.Text);
    }

    [Fact]
    public async Task Missing_final_falls_back_to_recorded_audio_after_timeout()
    {
        var factory = new FakeWebSocketFactory(_ =>
        {
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                switch (LiveServer.MessageType(msg))
                {
                    case "setup": s.ServerSend(LiveServer.SetupComplete); break;
                    case "audio": s.ServerSend(LiveServer.Interim("provisional only")); break;
                }
                return Task.CompletedTask;
            };
            return ws;
        });
        var recorded = new FakeRecordedTranscriber { Result = _ => "Recovered." };
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, recorded);
        session.Start();
        session.WriteAudio(Audio(16000));
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal("Recovered.", result.Text);
        Assert.True(result.UsedRecordedFallback);
    }

    [Fact]
    public async Task No_audio_means_no_transcript_and_no_activity_markers()
    {
        var factory = new FakeWebSocketFactory(_ => LiveServer.Transcribing("should not appear"));
        await using var session = new LiveTranscriptionSession(Options(), Key, factory, new FakeRecordedTranscriber());
        session.Start();
        await WaitUntil(() => factory.Created.Count == 1 && factory.Created[0].Sent.Count == 1);
        var result = await session.CompleteAsync(default).WaitAsync(s_testTimeout);

        Assert.Equal(string.Empty, result.Text);
        Assert.DoesNotContain(factory.Created[0].Sent, m => LiveServer.MessageType(m) is "activityStart" or "activityEnd");
    }

    [Fact]
    public async Task Cancellation_stops_promptly_and_closes_connections()
    {
        var factory = new FakeWebSocketFactory(_ =>
        {
            var ws = new FakeWebSocket();
            ws.OnClientMessage = (s, msg) =>
            {
                if (LiveServer.MessageType(msg) == "setup") s.ServerSend(LiveServer.SetupComplete);
                return Task.CompletedTask; // never answers activityEnd
            };
            return ws;
        });
        var options = Options() with { FinalizeTimeout = TimeSpan.FromSeconds(30) };
        var session = new LiveTranscriptionSession(options, Key, factory, new FakeRecordedTranscriber());
        session.Start();
        session.WriteAudio(Audio(6400));
        await WaitUntil(() => factory.Created.Count == 1 && factory.Created[0].AudioBytesSent == 6400);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompleteAsync(cts.Token).WaitAsync(s_testTimeout));
        await session.DisposeAsync().AsTask().WaitAsync(s_testTimeout);
        Assert.True(factory.Created[0].Disposed);
    }
}
