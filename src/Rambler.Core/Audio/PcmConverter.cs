using System.Buffers.Binary;
using NAudio.Dsp;

namespace Rambler.Core.Audio;

/// <summary>
/// Converts whatever the microphone delivers (any rate, any channel count, float32 / PCM16 / PCM24 / PCM32)
/// into 16 kHz mono signed 16-bit little-endian PCM, the format the Gemini Live API expects.
/// Buffers are reused between calls so the audio callback does not allocate in steady state.
/// </summary>
public sealed class PcmConverter
{
    public const int TargetSampleRate = 16000;

    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly WdlResampler? _resampler;
    private readonly double _ratio;
    private float[] _mono = new float[4096];
    private float[] _resampled = new float[4096];
    private byte[] _output = new byte[8192];

    public PcmConverter(int sampleRate, int channels, int bitsPerSample, bool isFloat)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (isFloat ? bitsPerSample != 32 : bitsPerSample is not (16 or 24 or 32))
            throw new NotSupportedException($"Unsupported microphone format: {bitsPerSample}-bit {(isFloat ? "float" : "PCM")}.");

        SourceSampleRate = sampleRate;
        _channels = channels;
        _bytesPerSample = bitsPerSample / 8;
        _isFloat = isFloat;
        _ratio = (double)TargetSampleRate / sampleRate;

        if (sampleRate != TargetSampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true); // input-driven
            _resampler.SetRates(sampleRate, TargetSampleRate);
        }
    }

    public int SourceSampleRate { get; }

    /// <summary>Peak of the last converted block, 0..1.</summary>
    public float LastPeak { get; private set; }

    /// <summary>RMS of the last converted block, 0..1.</summary>
    public float LastRms { get; private set; }

    /// <summary>
    /// Converts one capture block. The returned span is only valid until the next call.
    /// Trailing partial frames are ignored (capture APIs deliver whole frames).
    /// </summary>
    public ReadOnlySpan<byte> Convert(ReadOnlySpan<byte> input)
    {
        var frameBytes = _bytesPerSample * _channels;
        var frames = input.Length / frameBytes;
        if (frames == 0)
        {
            LastPeak = LastRms = 0;
            return [];
        }

        EnsureCapacity(ref _mono, frames);
        DownmixToMono(input, frames);
        MeasureLevel(_mono.AsSpan(0, frames));

        ReadOnlySpan<float> samples;
        if (_resampler is null)
        {
            samples = _mono.AsSpan(0, frames);
        }
        else
        {
            var inNeeded = _resampler.ResamplePrepare(frames, 1, out var inBuffer, out var inOffset);
            var toCopy = Math.Min(inNeeded, frames);
            Array.Copy(_mono, 0, inBuffer, inOffset, toCopy);

            var maxOut = (int)Math.Ceiling(frames * _ratio) + 16;
            EnsureCapacity(ref _resampled, maxOut);
            var produced = _resampler.ResampleOut(_resampled, 0, toCopy, maxOut, 1);
            samples = _resampled.AsSpan(0, produced);
        }

        EnsureCapacity(ref _output, samples.Length * 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            var s = v >= 1f ? short.MaxValue : v <= -1f ? short.MinValue : (short)MathF.Round(v * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(_output.AsSpan(i * 2), s);
        }

        return _output.AsSpan(0, samples.Length * 2);
    }

    private void DownmixToMono(ReadOnlySpan<byte> input, int frames)
    {
        var inv = 1f / _channels;
        var offset = 0;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < _channels; c++)
            {
                sum += ReadSample(input.Slice(offset, _bytesPerSample));
                offset += _bytesPerSample;
            }
            _mono[f] = sum * inv;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> b)
    {
        if (_isFloat) return BinaryPrimitives.ReadSingleLittleEndian(b);
        return _bytesPerSample switch
        {
            2 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
            3 => ((b[0] << 8 | b[1] << 16 | b[2] << 24) >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        };
    }

    private void MeasureLevel(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        double sumSquares = 0;
        foreach (var s in samples)
        {
            var a = MathF.Abs(s);
            if (a > peak) peak = a;
            sumSquares += s * s;
        }
        LastPeak = Math.Min(peak, 1f);
        LastRms = (float)Math.Min(Math.Sqrt(sumSquares / samples.Length), 1.0);
    }

    /// <summary>Maps RMS to a 0..1 meter value on a -60 dBFS..0 dBFS scale.</summary>
    public static float ToMeterLevel(float rms)
    {
        if (rms <= 0.000001f) return 0f;
        var db = 20f * MathF.Log10(rms);
        return Math.Clamp((db + 60f) / 60f, 0f, 1f);
    }

    private static void EnsureCapacity<T>(ref T[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new T[Math.Max(length, buffer.Length * 2)];
    }
}
