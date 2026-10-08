using System.Net;
using System.Text.Json;
using Rambler.Core.Gemini;
using Rambler.Core.Tests.Fakes;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests;

public class RecordedTranscriberTests
{
    private const string Key = "AIzaTESTKEY0000000000000000000000000000";
    private const string UploadUrl = "https://generativelanguage.googleapis.com/upload/v1beta/files?upload_id=abc";

    private static readonly RecordedTranscriptionOptions s_options = new("gemini-3.5-transcribe", true, null, ["Rambler"]);

    private static FakeHttpHandler HappyPath(string interactionBody) => new FakeHttpHandler()
        .Respond(HttpStatusCode.OK, "{}", r => r.Headers.Add("X-Goog-Upload-URL", UploadUrl))
        .Respond(HttpStatusCode.OK, """{"file":{"name":"files/xyz","uri":"https://generativelanguage.googleapis.com/v1beta/files/xyz","mimeType":"audio/wav"}}""")
        .Respond(HttpStatusCode.OK, interactionBody)
        .Respond(HttpStatusCode.OK, "{}");

    [Fact]
    public async Task Uploads_transcribes_with_smart_mode_then_deletes_the_file()
    {
        var handler = HappyPath("""{"id":"i1","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Hello there."}]}]}""");
        var transcriber = new GeminiRecordedTranscriber(new GeminiHttp(new HttpClient(handler)));

        var text = await transcriber.TranscribeAsync([1, 2, 3, 4], s_options, Key, CancellationToken.None);

        Assert.Equal("Hello there.", text);
        Assert.Equal(4, handler.Requests.Count);

        var start = handler.Requests[0];
        Assert.Equal("https://generativelanguage.googleapis.com/upload/v1beta/files", start.Uri.ToString());
        Assert.Equal("resumable", start.Headers["x-goog-upload-protocol"]);
        Assert.Equal("start", start.Headers["x-goog-upload-command"]);
        Assert.Equal("4", start.Headers["x-goog-upload-header-content-length"]);

        var upload = handler.Requests[1];
        Assert.Equal(UploadUrl, upload.Uri.ToString());
        Assert.Equal("upload, finalize", upload.Headers["x-goog-upload-command"]);

        var interaction = handler.Requests[2];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", interaction.Uri.ToString());
        using var body = JsonDocument.Parse(interaction.Body);
        Assert.Equal("gemini-3.5-transcribe", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        var input = body.RootElement.GetProperty("input")[0];
        Assert.Equal("audio", input.GetProperty("type").GetString());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/xyz", input.GetProperty("uri").GetString());
        var config = body.RootElement.GetProperty("generation_config").GetProperty("transcription_config");
        Assert.Equal("smart", config.GetProperty("mode").GetString());
        Assert.Equal("Rambler", config.GetProperty("custom_vocabulary")[0].GetString());

        var delete = handler.Requests[3];
        Assert.Equal(HttpMethod.Delete, delete.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/xyz", delete.Uri.ToString());

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(Key, r.Headers["x-goog-api-key"]);
            Assert.DoesNotContain(Key, r.Uri.ToString());
        });
    }

    [Fact]
    public async Task Deletes_the_upload_even_when_transcription_fails()
    {
        var handler = new FakeHttpHandler()
            .Respond(HttpStatusCode.OK, "{}", r => r.Headers.Add("X-Goog-Upload-URL", UploadUrl))
            .Respond(HttpStatusCode.OK, """{"file":{"name":"files/xyz","uri":"u"}}""")
            .Respond(HttpStatusCode.NotFound, """{"error":{"message":"model not found"}}""")
            .Respond(HttpStatusCode.OK, "{}");
        var transcriber = new GeminiRecordedTranscriber(new GeminiHttp(new HttpClient(handler)));

        var ex = await Assert.ThrowsAsync<GeminiException>(() => transcriber.TranscribeAsync([1], s_options, Key, default));
        Assert.Equal(GeminiErrorKind.ModelUnavailable, ex.Kind);
        Assert.Equal(HttpMethod.Delete, handler.Requests[^1].Method);
    }

    [Theory]
    [InlineData("""{"steps":[{"type":"user_input","content":[{"type":"text","text":"ignored"}]},{"type":"model_output","content":[{"type":"text","text":"A"},{"type":"text","text":"B"}]}]}""", "AB")]
    [InlineData("""{"outputs":[{"type":"text","text":"legacy"}]}""", "legacy")]
    [InlineData("""{"steps":[]}""", "")]
    public void Extracts_model_output_text(string json, string expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, GeminiRecordedTranscriber.ExtractOutputText(doc.RootElement));
    }
}
