namespace Rambler.Core.Gemini;

/// <summary>
/// Documented Gemini API endpoints (Gemini Developer API, v1beta).
/// Authentication is always the <c>x-goog-api-key</c> header, never a URL query parameter,
/// so keys cannot leak through URLs, proxies or exception messages.
/// </summary>
public static class GeminiEndpoints
{
    public const string ApiKeyHeader = "x-goog-api-key";
    public const string RestBase = "https://generativelanguage.googleapis.com";
    public const string WebSocketBase = "wss://generativelanguage.googleapis.com";

    /// <summary>Live API (BidiGenerateContent) used by gemini-3.5-transcribe-live.</summary>
    public static Uri LiveWebSocket { get; } =
        new($"{WebSocketBase}/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    public static Uri GenerateContent(string model) =>
        new($"{RestBase}/v1beta/{ModelResource(model)}:generateContent");

    public static Uri GetModel(string model) => new($"{RestBase}/v1beta/{ModelResource(model)}");

    /// <summary>Interactions API, used for recorded-audio transcription with gemini-3.5-transcribe.</summary>
    public static Uri Interactions { get; } = new($"{RestBase}/v1beta/interactions");

    /// <summary>Files API resumable upload start.</summary>
    public static Uri FileUpload { get; } = new($"{RestBase}/upload/v1beta/files");

    public static Uri File(string fileName) => new($"{RestBase}/v1beta/{fileName}");

    public static string ModelResource(string model)
    {
        model = model.Trim();
        return model.StartsWith("models/", StringComparison.Ordinal) ? model : "models/" + model;
    }
}
