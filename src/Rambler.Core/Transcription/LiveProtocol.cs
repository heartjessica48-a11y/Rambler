using System.Text;
using System.Text.Json;
using Rambler.Core.Gemini;

namespace Rambler.Core.Transcription;

public sealed record LiveSetupOptions(
    string Model,
    bool Smart,
    string? LanguageCode,
    IReadOnlyList<string> CustomVocabulary);

public enum LiveEventKind { SetupComplete, Interim, Final, TurnComplete, GoAway }

public readonly record struct LiveEvent(LiveEventKind Kind, string Text = "", bool Finished = false);

/// <summary>
/// Wire format for the Gemini Live API (BidiGenerateContent) as used by gemini-3.5-transcribe-live.
/// Field names follow the documented camelCase JSON mapping:
/// setup.inputAudioTranscription {mode, languageCodes, customVocabulary},
/// setup.realtimeInputConfig.automaticActivityDetection.disabled (manual endpointing, recommended for SMART),
/// realtimeInput {audio | activityStart | activityEnd}, serverContent {interimInputTranscription, inputTranscription, turnComplete}.
/// </summary>
public static class LiveProtocol
{
    public const string AudioMimeType = "audio/pcm;rate=16000";

    public static string BuildSetup(LiveSetupOptions o)
    {
        return Write(w =>
        {
            w.WriteStartObject("setup");
            w.WriteString("model", GeminiEndpoints.ModelResource(o.Model));

            w.WriteStartObject("generationConfig");
            w.WriteStartArray("responseModalities");
            w.WriteStringValue("TEXT");
            w.WriteEndArray();
            w.WriteEndObject();

            // Manual endpointing: the user decides when a thought is finished, so natural thinking
            // pauses never split or finalize the utterance early. We send activityStart/activityEnd.
            w.WriteStartObject("realtimeInputConfig");
            w.WriteStartObject("automaticActivityDetection");
            w.WriteBoolean("disabled", true);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("inputAudioTranscription");
            w.WriteString("mode", o.Smart ? "SMART" : "VERBATIM");
            if (!string.IsNullOrWhiteSpace(o.LanguageCode))
            {
                w.WriteStartArray("languageCodes");
                w.WriteStringValue(o.LanguageCode.Trim());
                w.WriteEndArray();
            }
            if (o.CustomVocabulary.Count > 0)
            {
                w.WriteStartArray("customVocabulary");
                foreach (var v in o.CustomVocabulary) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteEndObject();

            w.WriteEndObject();
        });
    }

    public static string BuildAudio(ReadOnlySpan<byte> pcm)
    {
        // {"realtimeInput":{"audio":{"data":"<base64>","mimeType":"audio/pcm;rate=16000"}}}
        var sb = new StringBuilder(64 + (pcm.Length + 2) / 3 * 4);
        sb.Append("{\"realtimeInput\":{\"audio\":{\"data\":\"");
        sb.Append(Convert.ToBase64String(pcm));
        sb.Append("\",\"mimeType\":\"").Append(AudioMimeType).Append("\"}}}");
        return sb.ToString();
    }

    public const string ActivityStart = "{\"realtimeInput\":{\"activityStart\":{}}}";
    public const string ActivityEnd = "{\"realtimeInput\":{\"activityEnd\":{}}}";

    /// <summary>
    /// Parses one server message. A single message can carry several parts (interim + final + turnComplete),
    /// so every part is returned in order. Unknown fields are ignored for forward compatibility.
    /// </summary>
    public static List<LiveEvent> Parse(string json)
    {
        var events = new List<LiveEvent>(2);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return events;

        if (root.TryGetProperty("setupComplete", out _))
            events.Add(new LiveEvent(LiveEventKind.SetupComplete));

        if (root.TryGetProperty("serverContent", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            if (TryGetTranscription(content, "interimInputTranscription", out var interim, out _))
                events.Add(new LiveEvent(LiveEventKind.Interim, interim));

            if (TryGetTranscription(content, "inputTranscription", out var final, out var finished))
                events.Add(new LiveEvent(LiveEventKind.Final, final, finished));

            if (content.TryGetProperty("turnComplete", out var tc) && tc.ValueKind == JsonValueKind.True)
                events.Add(new LiveEvent(LiveEventKind.TurnComplete));
        }

        if (root.TryGetProperty("goAway", out var goAway))
        {
            var timeLeft = goAway.ValueKind == JsonValueKind.Object && goAway.TryGetProperty("timeLeft", out var tl)
                ? tl.ToString()
                : string.Empty;
            events.Add(new LiveEvent(LiveEventKind.GoAway, timeLeft));
        }

        return events;
    }

    private static bool TryGetTranscription(JsonElement content, string name, out string text, out bool finished)
    {
        text = string.Empty;
        finished = false;
        if (!content.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Object) return false;
        if (t.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) text = tx.GetString() ?? string.Empty;
        if (t.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True) finished = true;
        return text.Length > 0 || finished;
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
