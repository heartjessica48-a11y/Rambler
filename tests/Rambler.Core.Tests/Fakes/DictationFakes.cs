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
    public event Action<CommittedSegment>? Committed;
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

    /// <summary>Like the real session: remaining text is emitted as a committed segment before completion.</summary>
    public bool EmitResultOnComplete { get; set; } = true;
    private int _nextIndex;

    public async Task<TranscriptionResult> CompleteAsync(CancellationToken ct)
    {
        if (CompletionGate is not null) await CompletionGate.Task.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (CompleteException is not null) throw CompleteException;
        if (EmitResultOnComplete && !string.IsNullOrWhiteSpace(Result.Text)) RaiseCommitted(_nextIndex, Result.Text);
        return Result;
    }

    public void RaisePreview(string text) => PreviewChanged?.Invoke(text);
    public void RaiseCommitted(int index, string text, double seconds = 3)
    {
        _nextIndex = index + 1;
        Committed?.Invoke(new CommittedSegment(index, text, TimeSpan.FromSeconds(seconds)));
    }
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
    private readonly object _gate = new();
    public List<string> Inputs { get; } = [];
    public List<CleanupRequest> Requests { get; } = [];
    public Func<string, string> Transform { get; set; } = t => "CLEAN: " + t;
    public Func<CleanupRequest, TimeSpan>? Delay { get; set; }
    public Exception? Throw { get; set; }
    public Func<CleanupRequest, Exception?>? ThrowFor { get; set; }

    public async Task<string> CleanAsync(CleanupRequest request, CleanupOptions options, string apiKey, CancellationToken ct)
    {
        lock (_gate)
        {
            Inputs.Add(request.Transcript);
            Requests.Add(request);
        }
        if (Delay?.Invoke(request) is { } d && d > TimeSpan.Zero) await Task.Delay(d, ct);
        if ((ThrowFor?.Invoke(request) ?? Throw) is { } ex) throw ex;
        return Transform(request.Transcript);
    }
}

public sealed class FakeInserter : ITextInserter
{
    private readonly object _gate = new();
    public List<(InsertionTarget Target, string Text)> Inserted { get; } = [];
    public List<InsertOptions> Options { get; } = [];
    public List<string> Copied { get; } = [];
    public InsertionTarget Target { get; set; } = new(42, 7, "notepad") { AppName = "Notepad", Kind = TargetKind.Editable };
    public Func<string, InsertionResult> Result { get; set; } = _ => InsertionResult.Success;

    /// <summary>Whether the captured target currently has focus.</summary>
    public volatile bool Ready = true;
    /// <summary>Whether an allowed refocus attempt succeeds.</summary>
    public bool RefocusSucceeds { get; set; } = true;
    public bool OwnWindowActive { get; set; }
    public int ActiveInsertions;
    public int MaxConcurrentInsertions;

    public event Action? FocusChanged;

    public string AllInsertedText { get { lock (_gate) return string.Concat(Inserted.Select(i => i.Text)); } }

    public InsertionTarget CaptureTarget() => Target;
    public bool IsTargetReady(InsertionTarget target) => Ready;
    public bool IsOwnWindowActive => OwnWindowActive;

    public void SetFocus(bool ready)
    {
        Ready = ready;
        FocusChanged?.Invoke();
    }

    public async Task<InsertionResult> InsertAsync(InsertionTarget target, string text, InsertOptions options, CancellationToken ct)
    {
        var active = Interlocked.Increment(ref ActiveInsertions);
        lock (_gate) MaxConcurrentInsertions = Math.Max(MaxConcurrentInsertions, active);
        try
        {
            await Task.Yield();
            lock (_gate) Options.Add(options);
            if (!Ready && options.AllowRefocus && RefocusSucceeds) Ready = true;
            if (!Ready)
            {
                if (options.CopyOnFailure)
                {
                    lock (_gate) Copied.Add(text);
                    return new InsertionResult(InsertionOutcome.CopiedToClipboard, "Focus moved. Text copied.");
                }
                return new InsertionResult(InsertionOutcome.TargetNotReady, "Not focused.");
            }
            var result = Result(text);
            if (result.Outcome == InsertionOutcome.Inserted) lock (_gate) Inserted.Add((target, text));
            if (result.Outcome == InsertionOutcome.TargetNotReady && result.CharsInserted > 0)
                lock (_gate) Inserted.Add((target, text[..result.CharsInserted]));
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref ActiveInsertions);
        }
    }

    public bool CopyToClipboard(string text)
    {
        lock (_gate) Copied.Add(text);
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
