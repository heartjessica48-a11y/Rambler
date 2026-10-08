using Rambler.Core.Settings;
using Rambler.Core.Transcription;

namespace Rambler.Core.Gemini;

public sealed record ConnectionCheck(string Name, bool Ok, string Detail);

/// <summary>Verifies the key and each configured model without sending any audio or transcript.</summary>
public sealed class GeminiConnectionTester(GeminiHttp http, IWebSocketFactory sockets)
{
    public async Task<IReadOnlyList<ConnectionCheck>> TestAsync(AppSettings settings, string apiKey, CancellationToken ct)
    {
        var results = new List<ConnectionCheck>
        {
            await CheckModelAsync("Live transcription model", settings.LiveModel, apiKey, ct).ConfigureAwait(false),
        };

        // A real Live API handshake: proves the WebSocket protocol and setup schema are accepted.
        try
        {
            var options = DictationLiveOptions(settings);
            await using var conn = await GeminiLiveConnection.OpenAsync(sockets, apiKey, options, TimeSpan.FromSeconds(12), ct)
                .ConfigureAwait(false);
            await conn.CloseAsync().ConfigureAwait(false);
            results.Add(new ConnectionCheck("Live session setup", true, "Setup accepted."));
        }
        catch (GeminiException ex)
        {
            results.Add(new ConnectionCheck("Live session setup", false, ex.UserMessage));
        }

        if (settings.UseRecordedFallback)
            results.Add(await CheckModelAsync("Recorded-audio fallback model", settings.RecordedModel, apiKey, ct).ConfigureAwait(false));
        results.Add(await CheckModelAsync("Cleanup model", settings.CleanupModel, apiKey, ct).ConfigureAwait(false));
        return results;
    }

    private static LiveSetupOptions DictationLiveOptions(AppSettings s) =>
        new(s.LiveModel, Smart: true, s.LanguageCode, s.CustomVocabulary);

    private async Task<ConnectionCheck> CheckModelAsync(string name, string model, string apiKey, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, GeminiEndpoints.GetModel(model)),
                apiKey, TimeSpan.FromSeconds(15), ct, retryTransient: false).ConfigureAwait(false);
            return new ConnectionCheck(name, true, $"{model} is available.");
        }
        catch (GeminiException ex)
        {
            return new ConnectionCheck(name, false, ex.Kind == GeminiErrorKind.ModelUnavailable ? $"{model} was not found." : ex.UserMessage);
        }
    }
}
