using Rambler.Core.Audio;
using Rambler.Core.Cleanup;
using Rambler.Core.Dictation;
using Rambler.Core.Gemini;
using Rambler.Core.Transcription;

namespace Rambler.Core.Tests.Fakes;

public sealed class FakeAudioSource : IAudioSource
{
    public event PcmDataHandler? DataAvailable;
    public event Action<float>? LevelChanged;
    public event Action<Exception>? Faulted;

    public bool IsCapturing { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public string? LastDeviceId { get; private set; }
    public Exception? StartException { get; set; }

    public void Start(string? deviceId)
    {
        if (StartException is not null) throw StartException;
        StartCount++;
        LastDeviceId = deviceId;
        IsCapturing = true;
    }

    public void Stop()
    {
        if (IsCapturing) StopCount++;
        IsCapturing = false;
    }

    public void Emit(byte[] pcm) => DataAvailable?.Invoke(pcm);
    public void EmitLevel(float level) => LevelChanged?.Invoke(level);
    public void Fault(Exception ex) => Faulted?.Invoke(ex);
    public void Dispose() => Stop();
}

public sealed class FakeTranscriptionSession : ITranscriptionSession
{
    public event Action<string>? PreviewChanged;
    public event Action<string>? Notice;
    public event Action<GeminiException>? Fatal;

    public TranscriptionResult Result { get; set; } = new("hello world", [], false);
    public TaskCompletionSource? CompletionGate { get; set; }
    public Exception? CompleteException { get; set; }
    public bool Started { get; private set; }
    public bool Disposed { get; private set; }
    public int AudioBytes;

    public void Start() => Started = true;
    public void WriteAudio(ReadOnlySpan<byte> pcm) => Interlocked.Add(ref AudioBytes, pcm.Length);

    public async Task<TranscriptionResult> CompleteAsync(CancellationToken ct)
    {
        if (CompletionGate is not null) await CompletionGate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (CompleteException is not null) throw CompleteException;
        return Result;
    }

    public void RaisePreview(string text) => PreviewChanged?.Invoke(text);
    public void RaiseNotice(string text) => Notice?.Invoke(text);
    public void RaiseFatal(GeminiException ex) => Fatal?.Invoke(ex);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeSessionFactory : ITranscriptionSessionFactory
{
    public List<FakeTranscriptionSession> Sessions { get; } = [];
    public Func<FakeTranscriptionSession> Next { get; set; } = () => new FakeTranscriptionSession();
    public TranscriptionOptions? LastOptions { get; private set; }
    public string? LastKey { get; private set; }

    public ITranscriptionSession Create(TranscriptionOptions options, string apiKey)
    {
        LastOptions = options;
        LastKey = apiKey;
        var s = Next();
        Sessions.Add(s);
        return s;
    }
}

public sealed class FakeCleanup : ICleanupService
{
    public List<string> Inputs { get; } = [];
    public Func<string, string> Transform { get; set; } = t => "CLEAN: " + t;
    public Exception? Throw { get; set; }

    public Task<string> CleanAsync(string transcript, CleanupOptions options, string apiKey, CancellationToken ct)
    {
        Inputs.Add(transcript);
        if (Throw is not null) throw Throw;
        return Task.FromResult(Transform(transcript));
    }
}

public sealed class FakeInserter : ITextInserter
{
    public List<(InsertionTarget Target, string Text)> Inserted { get; } = [];
    public List<string> Copied { get; } = [];
    public InsertionTarget Target { get; set; } = new(42, 7, "notepad");
    public Func<string, InsertionResult> Result { get; set; } = _ => InsertionResult.Success;

    public InsertionTarget CaptureTarget() => Target;

    public Task<InsertionResult> InsertAsync(InsertionTarget target, string text, CancellationToken ct)
    {
        var result = Result(text);
        if (result.Outcome == InsertionOutcome.Inserted) Inserted.Add((target, text));
        return Task.FromResult(result);
    }

    public bool CopyToClipboard(string text)
    {
        Copied.Add(text);
        return true;
    }
}

public sealed class FakeRecordedTranscriber : IRecordedTranscriber
{
    public List<byte[]> Calls { get; } = [];
    public Func<byte[], string> Result { get; set; } = _ => "recorded transcript";

    public Task<string> TranscribeAsync(byte[] wav, RecordedTranscriptionOptions options, string apiKey, CancellationToken ct)
    {
        Calls.Add(wav.ToArray());
        return Task.FromResult(Result(wav));
    }
}
