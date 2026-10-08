namespace Rambler.Core.Audio;

/// <summary>Receives 16 kHz mono PCM16 audio. The span is only valid during the call.</summary>
public delegate void PcmDataHandler(ReadOnlySpan<byte> pcm16kMono);

/// <summary>Microphone capture. Implementations must release the device in <see cref="Stop"/>.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Raised on the capture thread with converted audio.</summary>
    event PcmDataHandler? DataAvailable;

    /// <summary>Raised on the capture thread with a 0..1 meter level.</summary>
    event Action<float>? LevelChanged;

    /// <summary>Raised when capture ends on its own (device removed, driver error). Not raised by <see cref="Stop"/>.</summary>
    event Action<Exception>? Faulted;

    bool IsCapturing { get; }

    /// <summary>Starts capturing; throws <see cref="AudioDeviceException"/> when the microphone can't be opened.</summary>
    void Start(string? deviceId);

    /// <summary>Stops capturing and releases the microphone synchronously. Safe to call repeatedly.</summary>
    void Stop();
}

public enum AudioDeviceError { NotFound, AccessDenied, InUse, Unsupported, Unknown }

public sealed class AudioDeviceException(AudioDeviceError error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioDeviceError Error { get; } = error;
}

public sealed record AudioDeviceInfo(string Id, string Name);
