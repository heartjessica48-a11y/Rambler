using System.Net.WebSockets;
using System.Text.Json;
using Rambler.Core.Gemini;

namespace Rambler.Core.Transcription;

/// <summary>One WebSocket connection to the Gemini Live API, already past the setup handshake.</summary>
public sealed class GeminiLiveConnection : IAsyncDisposable
{
    private readonly IWebSocketConnection _ws;
    private int _disposed;

    private GeminiLiveConnection(IWebSocketConnection ws) => _ws = ws;

    public static async Task<GeminiLiveConnection> OpenAsync(IWebSocketFactory factory, string apiKey,
        LiveSetupOptions options, TimeSpan setupTimeout, CancellationToken ct)
    {
        var ws = factory.Create();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(setupTimeout);
        try
        {
            var headers = new Dictionary<string, string> { [GeminiEndpoints.ApiKeyHeader] = apiKey };
            await ws.ConnectAsync(GeminiEndpoints.LiveWebSocket, headers, timeout.Token).ConfigureAwait(false);
            await ws.SendTextAsync(LiveProtocol.BuildSetup(options), timeout.Token).ConfigureAwait(false);

            while (true)
            {
                var message = await ws.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (message is null) throw MapClose(ws.CloseStatus, ws.CloseStatusDescription);
                if (LiveProtocol.Parse(message).Any(e => e.Kind == LiveEventKind.SetupComplete))
                    return new GeminiLiveConnection(ws);
            }
        }
        catch (Exception ex)
        {
            await ws.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw ex as GeminiException ?? MapException(ex);
        }
    }

    public Task SendAsync(string message, CancellationToken ct) => _ws.SendTextAsync(message, ct);

    /// <summary>Next batch of events, or null when the server closed the connection (see <see cref="CloseError"/>).</summary>
    public async Task<List<LiveEvent>?> ReceiveEventsAsync(CancellationToken ct)
    {
        var message = await _ws.ReceiveAsync(ct).ConfigureAwait(false);
        if (message is null) return null;
        try
        {
            return LiveProtocol.Parse(message);
        }
        catch (JsonException ex)
        {
            throw new GeminiException(GeminiErrorKind.BadRequest, "Unexpected message format from the Live API.", ex);
        }
    }

    public GeminiException CloseError() => MapClose(_ws.CloseStatus, _ws.CloseStatusDescription);

    public async Task CloseAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _ws.CloseAsync(cts.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await _ws.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Maps a Live API close frame (status + description) to a typed error.</summary>
    public static GeminiException MapClose(WebSocketCloseStatus? status, string? description)
    {
        var desc = string.IsNullOrWhiteSpace(description) ? "Connection closed by server." : description.Trim();
        var kind = Classify(desc) ?? status switch
        {
            WebSocketCloseStatus.InvalidPayloadData => GeminiErrorKind.BadRequest,
            WebSocketCloseStatus.PolicyViolation => GeminiErrorKind.BadRequest,
            WebSocketCloseStatus.InternalServerError => GeminiErrorKind.Server,
            WebSocketCloseStatus.EndpointUnavailable => GeminiErrorKind.Server,
            _ => GeminiErrorKind.Network,
        };
        return new GeminiException(kind, desc);
    }

    internal static GeminiErrorKind? Classify(string text)
    {
        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("API_KEY", StringComparison.Ordinal) ||
            text.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("'401'", StringComparison.Ordinal) || text.Contains("'403'", StringComparison.Ordinal))
            return GeminiErrorKind.InvalidApiKey;
        if (text.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal) ||
            text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("'429'", StringComparison.Ordinal))
            return GeminiErrorKind.QuotaExceeded;
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("is not available", StringComparison.OrdinalIgnoreCase) || text.Contains("'404'", StringComparison.Ordinal))
            return GeminiErrorKind.ModelUnavailable;
        return null;
    }

    private static GeminiException MapException(Exception ex) => ex switch
    {
        OperationCanceledException => new GeminiException(GeminiErrorKind.Timeout, "Timed out connecting to the Live API."),
        WebSocketException w => new GeminiException(Classify(w.Message) ?? GeminiErrorKind.Network, "Live API connection failed: " + w.Message, w),
        JsonException j => new GeminiException(GeminiErrorKind.BadRequest, "Unexpected message format from the Live API.", j),
        _ => new GeminiException(GeminiErrorKind.Network, "Live API connection failed: " + ex.Message, ex),
    };

    internal static GeminiException MapStreamException(Exception ex) =>
        ex as GeminiException ?? MapException(ex);
}
