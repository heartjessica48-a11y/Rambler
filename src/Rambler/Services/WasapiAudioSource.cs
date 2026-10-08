using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rambler.Core.Audio;

namespace Rambler.Services;

/// <summary>
/// WASAPI shared-mode microphone capture. Accepts whatever format the device mixes in and converts it
/// to 16 kHz mono PCM16. The device is opened only while recording and released synchronously on Stop.
/// </summary>
public sealed class WasapiAudioSource : IAudioSource
{
    private static readonly Guid s_floatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);

    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private PcmConverter? _converter;
    private long _lastLevelTick;

    public event PcmDataHandler? DataAvailable;
    public event Action<float>? LevelChanged;
    public event Action<Exception>? Faulted;

    public bool IsCapturing { get { lock (_gate) return _capture is not null; } }

    /// <summary>Name of the microphone currently in use (for display).</summary>
    public string? CurrentDeviceName { get; private set; }

    /// <summary>Set when the selected microphone was unavailable and the default was used instead.</summary>
    public bool UsedDefaultFallback { get; private set; }

    public static IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d) list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
            }
        }
        catch (COMException)
        {
            // No audio subsystem: return empty.
        }
        return list;
    }

    public void Start(string? deviceId)
    {
        lock (_gate)
        {
            if (_capture is not null) return;

            MMDevice device;
            try
            {
                device = OpenDevice(deviceId);
            }
            catch (COMException ex)
            {
                throw Map(ex);
            }

            WasapiCapture capture;
            try
            {
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
                var format = capture.WaveFormat;
                var isFloat = format is WaveFormatExtensible ext
                    ? ext.SubFormat == s_floatSubFormat
                    : format.Encoding == WaveFormatEncoding.IeeeFloat;
                _converter = new PcmConverter(format.SampleRate, format.Channels, format.BitsPerSample, isFloat);

                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException)
            {
                device.Dispose();
                throw ex is COMException com ? Map(com)
                    : new AudioDeviceException(AudioDeviceError.Unsupported, "This microphone's audio format isn't supported: " + ex.Message, ex);
            }

            _device = device;
            _capture = capture;
            CurrentDeviceName = device.FriendlyName;
        }
    }

    public void Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        lock (_gate)
        {
            capture = _capture;
            device = _device;
            _capture = null;
            _device = null;
        }
        if (capture is null) return;

        capture.DataAvailable -= OnDataAvailable;
        try
        {
            capture.StopRecording();
            capture.Dispose(); // joins the capture thread and releases the audio client
        }
        catch (COMException)
        {
            // Device already gone.
        }
        device?.Dispose();
        LevelChanged?.Invoke(0f);
    }

    private MMDevice OpenDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        UsedDefaultFallback = false;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                var selected = enumerator.GetDevice(deviceId);
                if (selected.State == DeviceState.Active) return selected;
                selected.Dispose();
            }
            catch (COMException)
            {
                // Selected microphone was removed; fall back to the default.
            }
            UsedDefaultFallback = true;
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        }
        catch (COMException ex) when (ex.HResult == E_NOTFOUND)
        {
            throw new AudioDeviceException(AudioDeviceError.NotFound, "No microphone found. Connect one and try again.", ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var converter = _converter;
        if (converter is null || e.BytesRecorded == 0) return;

        var pcm = converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
        if (!pcm.IsEmpty) DataAvailable?.Invoke(pcm);

        var now = Environment.TickCount64;
        if (now - _lastLevelTick >= 50)
        {
            _lastLevelTick = now;
            LevelChanged?.Invoke(PcmConverter.ToMeterLevel(converter.LastRms));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool unexpected;
        lock (_gate) unexpected = ReferenceEquals(sender, _capture);
        if (!unexpected) return; // normal Stop()

        var error = e.Exception is COMException com
            ? Map(com)
            : new AudioDeviceException(AudioDeviceError.Unknown, "The microphone stopped unexpectedly.", e.Exception);
        // Release on a worker thread: never join the capture thread from its own callbacks.
        ThreadPool.QueueUserWorkItem(_ => Stop());
        Faulted?.Invoke(error);
    }

    private static AudioDeviceException Map(COMException ex) => ex.HResult switch
    {
        E_ACCESSDENIED => new AudioDeviceException(AudioDeviceError.AccessDenied,
            "Microphone access is blocked. Allow it in Windows Settings › Privacy & security › Microphone (\"Let desktop apps access your microphone\").", ex),
        AUDCLNT_E_DEVICE_IN_USE => new AudioDeviceException(AudioDeviceError.InUse,
            "The microphone is in exclusive use by another app.", ex),
        AUDCLNT_E_DEVICE_INVALIDATED or E_NOTFOUND => new AudioDeviceException(AudioDeviceError.NotFound,
            "The microphone was disconnected.", ex),
        _ => new AudioDeviceException(AudioDeviceError.Unknown, $"Microphone error (0x{ex.HResult:X8}).", ex),
    };

    public void Dispose() => Stop();
}
