using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambler.Core.Gemini;

namespace Rambler.Core.Transcription;

public sealed record RecordedTranscriptionOptions(
    string Model,
    bool Smart,
    string? LanguageCode,
    IReadOnlyList<string> CustomVocabulary);

public interface IRecordedTranscriber
{
    Task<string> TranscribeAsync(byte[] wav, RecordedTranscriptionOptions options, string apiKey, CancellationToken ct);
}

/// <summary>
/// Recorded-audio transcription with gemini-3.5-transcribe, mirroring the working proof of concept:
/// upload WAV via the Files API, call the Interactions API with transcription_config.mode = "smart",
/// then delete the uploaded file. Requests set store=false so the interaction isn't retained for retrieval.
/// </summary>
public sealed class GeminiRecordedTranscriber(GeminiHttp http) : IRecordedTranscriber
{
    private static readonly TimeSpan s_uploadTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_transcribeTimeout = TimeSpan.FromMinutes(5);

    public async Task<string> TranscribeAsync(byte[] wav, RecordedTranscriptionOptions options, string apiKey, CancellationToken ct)
    {
        var file = await UploadAsync(wav, apiKey, ct).ConfigureAwait(false);
        try
        {
            using var doc = await http.PostJsonAsync(GeminiEndpoints.Interactions, apiKey,
                BuildInteractionJson(file.Uri, file.MimeType, options), s_transcribeTimeout, ct).ConfigureAwait(false);
            var text = ExtractOutputText(doc.RootElement).Trim();
            if (text.Length == 0)
                throw new GeminiException(GeminiErrorKind.EmptyResult, "The transcription model returned no text.");
            return text;
        }
        finally
        {
            await TryDeleteAsync(file.Name, apiKey).ConfigureAwait(false);
        }
    }

    internal static string BuildInteractionJson(string fileUri, string mimeType, RecordedTranscriptionOptions o)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("model", o.Model.Trim());
            w.WriteStartArray("input");
            w.WriteStartObject();
            w.WriteString("type", "audio");
            w.WriteString("uri", fileUri);
            w.WriteString("mime_type", mimeType);
            w.WriteEndObject();
            w.WriteEndArray();

            w.WriteStartObject("generation_config");
            w.WriteStartObject("transcription_config");
            w.WriteString("mode", o.Smart ? "smart" : "verbatim");
            if (!string.IsNullOrWhiteSpace(o.LanguageCode))
            {
                w.WriteStartArray("language_codes");
                w.WriteStringValue(o.LanguageCode.Trim());
                w.WriteEndArray();
            }
            if (o.CustomVocabulary.Count > 0)
            {
                w.WriteStartArray("custom_vocabulary");
                foreach (var v in o.CustomVocabulary) w.WriteStringValue(v);
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteBoolean("store", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Text of the trailing model output (steps[].type == "model_output"), with legacy "outputs" support.</summary>
    internal static string ExtractOutputText(JsonElement root)
    {
        var sb = new StringBuilder();
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("type", out var type) && type.GetString() == "model_output" &&
                    step.TryGetProperty("content", out var content))
                {
                    AppendText(content, sb);
                }
            }
        }
        else if (root.TryGetProperty("outputs", out var outputs))
        {
            AppendText(outputs, sb);
        }
        return sb.ToString();
    }

    private static void AppendText(JsonElement content, StringBuilder sb)
    {
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
    }

    private sealed record UploadedFile(string Name, string Uri, string MimeType);

    /// <summary>Files API resumable upload: start (metadata) → upload, finalize (bytes).</summary>
    private async Task<UploadedFile> UploadAsync(byte[] wav, string apiKey, CancellationToken ct)
    {
        const string mime = "audio/wav";
        string uploadUrl;
        using (var start = await http.SendAsync(() =>
               {
                   var req = new HttpRequestMessage(HttpMethod.Post, GeminiEndpoints.FileUpload)
                   {
                       Content = new StringContent("{\"file\":{\"display_name\":\"rambler-dictation\"}}", Encoding.UTF8, "application/json"),
                   };
                   req.Headers.Add("X-Goog-Upload-Protocol", "resumable");
                   req.Headers.Add("X-Goog-Upload-Command", "start");
                   req.Headers.Add("X-Goog-Upload-Header-Content-Length", wav.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                   req.Headers.Add("X-Goog-Upload-Header-Content-Type", mime);
                   return req;
               }, apiKey, s_uploadTimeout, ct).ConfigureAwait(false))
        {
            uploadUrl = start.Headers.TryGetValues("X-Goog-Upload-URL", out var values) ? values.FirstOrDefault() ?? "" : "";
            if (uploadUrl.Length == 0)
                throw new GeminiException(GeminiErrorKind.BadRequest, "Files API did not return an upload URL.");
        }

        using var finish = await http.SendAsync(() =>
        {
            var content = new ByteArrayContent(wav);
            content.Headers.ContentType = new MediaTypeHeaderValue(mime);
            var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = content };
            req.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            req.Headers.Add("X-Goog-Upload-Offset", "0");
            return req;
        }, apiKey, s_uploadTimeout, ct).ConfigureAwait(false);

        using var doc = await GeminiHttp.ReadJsonAsync(finish, ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("file", out var file) ||
            !file.TryGetProperty("name", out var name) || !file.TryGetProperty("uri", out var uri))
        {
            throw new GeminiException(GeminiErrorKind.BadRequest, "Unexpected Files API response.");
        }
        var mimeType = file.TryGetProperty("mimeType", out var m) ? m.GetString() ?? mime : mime;
        return new UploadedFile(name.GetString()!, uri.GetString()!, mimeType);
    }

    private async Task TryDeleteAsync(string fileName, string apiKey)
    {
        try
        {
            using var _ = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, GeminiEndpoints.File(fileName)),
                apiKey, TimeSpan.FromSeconds(15), CancellationToken.None, retryTransient: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Uploaded files also expire automatically on Google's side; never fail a transcript over cleanup.
            AppLog.Warn("Could not delete uploaded audio file: " + ex.Message);
        }
    }
}
