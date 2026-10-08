using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Rambler.Core.Gemini;

/// <summary>Thin helper over a shared <see cref="HttpClient"/>: auth header, error mapping, one retry.</summary>
public sealed class GeminiHttp(HttpClient http)
{
    public HttpClient Client { get; } = http;

    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts via CancellationToken
        };

    public async Task<JsonDocument> PostJsonAsync(Uri uri, string apiKey, string json, TimeSpan timeout,
        CancellationToken ct, bool retryTransient = true)
    {
        using var response = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            return req;
        }, apiKey, timeout, ct, retryTransient).ConfigureAwait(false);

        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, string apiKey,
        TimeSpan timeout, CancellationToken ct, bool retryTransient = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            using var request = factory();
            request.Headers.Remove(GeminiEndpoints.ApiKeyHeader);
            request.Headers.TryAddWithoutValidation(GeminiEndpoints.ApiKeyHeader, apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new GeminiException(GeminiErrorKind.Timeout, "Request timed out.");
            }
            catch (HttpRequestException ex)
            {
                throw new GeminiException(GeminiErrorKind.Network, "Network error: " + ex.Message, ex);
            }

            if (response.IsSuccessStatusCode) return response;

            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            var error = MapHttpError(response.StatusCode, body);
            var transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            response.Dispose();

            if (retryTransient && transient && attempt == 0)
            {
                var delay = retryAfter is { } d && d < TimeSpan.FromSeconds(5) ? d : TimeSpan.FromSeconds(1.5);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            throw error;
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GeminiException(GeminiErrorKind.BadRequest, "Unexpected response format from Gemini.", ex);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    /// <summary>Maps a REST error (google.rpc.Status JSON) to a typed exception.</summary>
    public static GeminiException MapHttpError(HttpStatusCode status, string body)
    {
        var (message, reason) = ParseErrorBody(body);
        message = string.IsNullOrWhiteSpace(message) ? $"HTTP {(int)status} {status}" : message;
        if (message.Length > 300) message = message[..300] + "…";

        var kind = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => GeminiErrorKind.InvalidApiKey,
            HttpStatusCode.TooManyRequests => GeminiErrorKind.QuotaExceeded,
            HttpStatusCode.NotFound => GeminiErrorKind.ModelUnavailable,
            HttpStatusCode.BadRequest when IsKeyProblem(message, reason) => GeminiErrorKind.InvalidApiKey,
            HttpStatusCode.BadRequest => GeminiErrorKind.BadRequest,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => GeminiErrorKind.Timeout,
            >= HttpStatusCode.InternalServerError => GeminiErrorKind.Server,
            _ => GeminiErrorKind.Unknown,
        };
        return new GeminiException(kind, message);
    }

    internal static bool IsKeyProblem(string message, string? reason) =>
        reason is "API_KEY_INVALID" or "API_KEY_EXPIRED"
        || message.Contains("API key", StringComparison.OrdinalIgnoreCase);

    private static (string? Message, string? Reason) ParseErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (!root.TryGetProperty("error", out var err)) return (null, null);

            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
            string? reason = null;
            if (err.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in details.EnumerateArray())
                    if (d.TryGetProperty("reason", out var r)) { reason = r.GetString(); break; }
            }
            return (msg, reason);
        }
        catch (JsonException)
        {
            return (body.Length > 200 ? body[..200] : body, null);
        }
    }
}
