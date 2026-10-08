using System.Buffers.Binary;

namespace Rambler.Core.Audio;

/// <summary>Wraps raw PCM in a canonical 44-byte RIFF/WAVE header (in memory; nothing is written to disk).</summary>
public static class WavWriter
{
    public static byte[] ToWav(ReadOnlySpan<byte> pcm, int sampleRate = PcmConverter.TargetSampleRate,
        short channels = 1, short bitsPerSample = 16)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        var blockAlign = (short)(channels * bitsPerSample / 8);

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], bitsPerSample);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }
}
