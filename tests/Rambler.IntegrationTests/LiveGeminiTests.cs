using System.Buffers.Binary;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Settings;
using Rambler.Core.Transcription;
using Xunit.Abstractions;

namespace Rambler.IntegrationTests;

/// <summary>Runs only when GEMINI_API_KEY is set. Uses the real Gemini API (costs a little quota).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(bool needsWav = false)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
            Skip = "Set GEMINI_API_KEY to run live Gemini tests.";
        else if (needsWav && !File.Exists(Environment.GetEnvironmentVariable("RAMBLER_TEST_WAV") ?? ""))
            Skip = "Set RAMBLER_TEST_WAV to a 16 kHz mono 16-bit WAV file with speech.";
    }
}

public class LiveGeminiTests(ITestOutputHelper output)
{
    private static string Key => Environment.GetEnvironmentVariable("GEMINI_API_KEY")!.Trim();
    private static readonly HttpClient s_http = GeminiHttp.CreateDefaultClient();
    private static GeminiHttp Gemini => new(s_http);

    [LiveFact]
    public async Task Connection_test_accepts_key_models_and_live_setup()
    {
        var tester = new GeminiConnectionTester(Gemini, new ClientWebSocketFactory());
        var results = await tester.TestAsync(new AppSettings(), Key, CancellationToken.None);
        foreach (var r in results) output.WriteLine($"{(r.Ok ? "OK  " : "FAIL")} {r.Name}: {r.Detail}");
        Assert.All(results, r => Assert.True(r.Ok, $"{r.Name}: {r.Detail}"));
    }

    [LiveFact]
    public async Task Prompt_cleanup_returns_edited_text_without_answering_it()
    {
        var service = new GeminiCleanupService(Gemini);
        var options = DictationCoordinator.BuildCleanupOptions(new AppSettings());
        const string transcript = "um so like what's the uh the fastest way to, no wait, the safest way to rotate API keys in a fucking monorepo";
        var cleaned = await service.CleanAsync(transcript, options, Key, CancellationToken.None);
        output.WriteLine(cleaned);
        Assert.False(string.IsNullOrWhiteSpace(cleaned));
        Assert.Contains("safest", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact(needsWav: true)]
    public async Task Recorded_audio_transcription_returns_text()
    {
        var wav = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("RAMBLER_TEST_WAV")!);
        var transcriber = new GeminiRecordedTranscriber(Gemini);
        var text = await transcriber.TranscribeAsync(wav,
            new RecordedTranscriptionOptions(AppSettings.DefaultRecordedModel, true, null, []), Key, CancellationToken.None);
        output.WriteLine(text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [LiveFact(needsWav: true)]
    public async Task Live_streaming_transcription_returns_text()
    {
        var pcm = ReadPcm16kMono(Environment.GetEnvironmentVariable("RAMBLER_TEST_WAV")!);
        var options = DictationCoordinator.BuildTranscriptionOptions(new AppSettings { UseRecordedFallback = false });
        await using var session = new LiveTranscriptionSession(options, Key, new ClientWebSocketFactory(),
            new GeminiRecordedTranscriber(Gemini));
        session.PreviewChanged += p => output.WriteLine("preview: " + p);
        session.Notice += n => output.WriteLine("notice: " + n);

        session.Start();
        for (var offset = 0; offset < pcm.Length; offset += 3200) // real-time pacing, 100 ms chunks
        {
            session.WriteAudio(pcm.AsSpan(offset, Math.Min(3200, pcm.Length - offset)));
            await Task.Delay(100);
        }
        var result = await session.CompleteAsync(CancellationToken.None);
        output.WriteLine("final: " + result.Text);
        foreach (var w in result.Warnings) output.WriteLine("warning: " + w);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.False(result.UsedRecordedFallback);
    }

    private static byte[] ReadPcm16kMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
        var bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(34));
        Assert.True(channels == 1 && rate == 16000 && bits == 16, "RAMBLER_TEST_WAV must be 16 kHz mono 16-bit PCM.");

        var pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
            if (id == "data") return bytes.AsSpan(pos + 8, Math.Min(size, bytes.Length - pos - 8)).ToArray();
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk in WAV file.");
    }
}
