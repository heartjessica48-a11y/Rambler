using System.Net.WebSockets;
using System.Text;

namespace Rambler.Core.Transcription;

/// <summary>Minimal text-message WebSocket surface so the Live protocol can be tested without a network.</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
    Task SendTextAsync(string message, CancellationToken ct);

    /// <summary>Returns the next complete message (text or binary decoded as UTF-8), or null when the server closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    WebSocketCloseStatus? CloseStatus { get; }
    string? CloseStatusDescription { get; }
    Task CloseAsync(CancellationToken ct);
}

public interface IWebSocketFactory
{
    IWebSocketConnection Create();
}

public sealed class ClientWebSocketFactory : IWebSocketFactory
{
    public IWebSocketConnection Create() => new ClientWebSocketConnection();
}

public sealed class ClientWebSocketConnection : IWebSocketConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _receiveBuffer = new byte[16 * 1024];
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;
    public string? CloseStatusDescription => _socket.CloseStatusDescription;

    public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        foreach (var (name, value) in headers) _socket.Options.SetRequestHeader(name, value);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async Task SendTextAsync(string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(_receiveBuffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing is best effort.
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
