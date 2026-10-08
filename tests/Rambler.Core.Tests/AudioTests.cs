using System.Buffers.Binary;
using Rambler.Core.Audio;

namespace Rambler.Core.Tests;

public class PcmConverterTests
{
    [Fact]
    public void Passes_16k_mono_pcm16_through_unchanged()
    {
        var input = new byte[3200];
        for (var i = 0; i < 1600; i++) BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2), (short)(i * 13 - 8000));
        var converter = new PcmConverter(16000, 1, 16, isFloat: false);

        var output = converter.Convert(input).ToArray();

        Assert.Equal(input.Length, output.Length);
        for (var i = 0; i < 1600; i++)
        {
            var a = BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(i * 2));
            var b = BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(i * 2));
            Assert.InRange(b - a, -1, 1);
        }
    }

    [Fact]
    public void Downmixes_and_resamples_48k_stereo_float_to_16k_mono()
    {
        var converter = new PcmConverter(48000, 2, 32, isFloat: true);
        var produced = 0;
        double sumSquares = 0;
        // 1 second of a 440 Hz tone, delivered in 10 ms blocks like WASAPI does.
        for (var block = 0; block < 100; block++)
        {
            var input = new byte[480 * 2 * 4];
            for (var f = 0; f < 480; f++)
            {
                var t = (block * 480 + f) / 48000.0;
                var v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t));
                BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(f * 8), v);
                BinaryPrimitives.WriteSingleLittleEndian(input.AsSpan(f * 8 + 4), v);
            }
            var output = converter.Convert(input);
            produced += output.Length / 2;
            for (var i = 0; i < output.Length / 2; i++)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(output.Slice(i * 2)) / 32768.0;
                sumSquares += s * s;
            }
        }

        Assert.InRange(produced, 15800, 16200); // ~1 s at 16 kHz
        var rms = Math.Sqrt(sumSquares / produced);
        Assert.InRange(rms, 0.30, 0.40); // 0.5 amplitude sine ≈ 0.354 RMS: level preserved
    }

    [Fact]
    public void Converts_24_bit_pcm()
    {
        var converter = new PcmConverter(16000, 1, 24, isFloat: false);
        // Max positive and max negative 24-bit samples.
        byte[] input = [0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80];
        var output = converter.Convert(input);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(output));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(output[2..]));
    }

    [Fact]
    public void Reports_levels()
    {
        var converter = new PcmConverter(16000, 1, 16, isFloat: false);
        converter.Convert(new byte[320]);
        Assert.Equal(0f, converter.LastRms);
        Assert.Equal(0f, PcmConverter.ToMeterLevel(converter.LastRms));

        var loud = new byte[320];
        for (var i = 0; i < 160; i++) BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(i * 2), (short)(i % 2 == 0 ? 16000 : -16000));
        converter.Convert(loud);
        Assert.InRange(converter.LastRms, 0.45f, 0.5f);
        Assert.InRange(PcmConverter.ToMeterLevel(converter.LastRms), 0.85f, 1f);
    }

    [Fact]
    public void Rejects_unsupported_formats()
    {
        Assert.Throws<NotSupportedException>(() => new PcmConverter(16000, 1, 8, isFloat: false));
        Assert.Throws<NotSupportedException>(() => new PcmConverter(16000, 1, 64, isFloat: true));
    }
}

public class WavWriterTests
{
    [Fact]
    public void Writes_a_canonical_16k_mono_header()
    {
        var wav = WavWriter.ToWav(new byte[100]);
        Assert.Equal(144, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(136, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); // mono
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }
}
