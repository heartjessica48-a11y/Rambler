using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests.Fakes;

/// <summary>In-memory WebSocket whose "server" side is driven by a script reacting to client messages.</summary>
public sealed class FakeWebSocket : IWebSocketConnection
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private readonly object _gate = new();
    private readonly List<string> _sent = [];

    public Uri? Uri { get; private set; }
    public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();
    public Exception? ConnectException { get; set; }
    public Func<FakeWebSocket, string, Task>? OnClientMessage { get; set; }
    public bool Disposed { get; private set; }
    public bool ClosedByServer { get; private set; }
    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public string? CloseStatusDescription { get; private set; }

    public IReadOnlyList<string> Sent { get { lock (_gate) return [.. _sent]; } }

    public int AudioBytesSent
    {
        get
        {
            var total = 0;
            foreach (var m in Sent)
            {
                using var doc = JsonDocument.Parse(m);
                if (doc.RootElement.TryGetProperty("realtimeInput", out var ri) && ri.TryGetProperty("audio", out var audio))
                    total += Convert.FromBase64String(audio.GetProperty("data").GetString()!).Length;
            }
            return total;
        }
    }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (ConnectException is not null) throw ConnectException;
        Uri = uri;
        Headers = new Dictionary<string, string>(headers);
        return Task.CompletedTask;
    }

    public async Task SendTextAsync(string message, CancellationToken ct)
    {
        if (ClosedByServer || Disposed) throw new WebSocketException("Connection closed.");
        lock (_gate) _sent.Add(message);
        if (OnClientMessage is not null) await OnClientMessage(this, message);
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeWebSocket));
        try
        {
            return await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new WebSocketException("Socket disposed.");
        }
    }

    public void ServerSend(string json) => _incoming.Writer.TryWrite(json);

    public void ServerClose(WebSocketCloseStatus status, string description)
    {
        CloseStatus = status;
        CloseStatusDescription = description;
        ClosedByServer = true;
        _incoming.Writer.TryWrite(null);
    }

    public Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeWebSocketFactory(Func<int, FakeWebSocket> create) : IWebSocketFactory
{
    private readonly object _gate = new();
    private readonly List<FakeWebSocket> _created = [];

    public IReadOnlyList<FakeWebSocket> Created { get { lock (_gate) return [.. _created]; } }

    public IWebSocketConnection Create()
    {
        lock (_gate)
        {
            var ws = create(_created.Count);
            _created.Add(ws);
            return ws;
        }
    }
}

/// <summary>Helpers that emulate the Gemini Live transcription server.</summary>
public static class LiveServer
{
    public const string SetupComplete = """{"setupComplete":{}}""";

    public static string Interim(string text) =>
        JsonSerializer.Serialize(new { serverContent = new { interimInputTranscription = new { text } } });

    public static string Final(string text, bool finished = false) =>
        JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text, finished } } });

    public const string TurnComplete = """{"serverContent":{"turnComplete":true}}""";

    public static string MessageType(string clientMessage)
    {
        using var doc = JsonDocument.Parse(clientMessage);
        var root = doc.RootElement;
        if (root.TryGetProperty("setup", out _)) return "setup";
        if (root.TryGetProperty("realtimeInput", out var ri))
        {
            if (ri.TryGetProperty("audio", out _)) return "audio";
            if (ri.TryGetProperty("activityStart", out _)) return "activityStart";
            if (ri.TryGetProperty("activityEnd", out _)) return "activityEnd";
        }
        return "other";
    }

    /// <summary>
    /// A well-behaved server: completes setup, sends interims, and finals on activityEnd.
    /// Like the real service, pure silence (all-zero audio) produces no transcript.
    /// </summary>
    public static FakeWebSocket Transcribing(params string[] finals)
    {
        var ws = new FakeWebSocket();
        var audioMessages = 0;
        var heardSound = false;
        ws.OnClientMessage = (socket, msg) =>
        {
            switch (MessageType(msg))
            {
                case "setup":
                    socket.ServerSend(SetupComplete);
                    break;
                case "audio":
                    audioMessages++;
                    using (var doc = JsonDocument.Parse(msg))
                    {
                        var data = Convert.FromBase64String(doc.RootElement.GetProperty("realtimeInput").GetProperty("audio").GetProperty("data").GetString()!);
                        if (data.Any(b => b != 0)) heardSound = true;
                    }
                    if (heardSound) socket.ServerSend(Interim($"partial {audioMessages}"));
                    break;
                case "activityEnd":
                    if (heardSound) foreach (var f in finals) socket.ServerSend(Final(f));
                    socket.ServerSend(TurnComplete);
                    break;
            }
            return Task.CompletedTask;
        };
        return ws;
    }
}
