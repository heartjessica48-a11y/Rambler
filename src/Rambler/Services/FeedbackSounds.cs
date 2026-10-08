using System.Buffers.Binary;
using System.IO;
using System.Media;
using Rambler.Core.Audio;

namespace Rambler.Services;

/// <summary>Optional short start/stop chimes, synthesized in memory (no files, no extra dependencies).</summary>
public sealed class FeedbackSounds : IDisposable
{
    private readonly SoundPlayer _start = new(new MemoryStream(Tone(660, 880)));
    private readonly SoundPlayer _stop = new(new MemoryStream(Tone(880, 660)));

    public void PlayStart() => Play(_start);
    public void PlayStop() => Play(_stop);

    private static void Play(SoundPlayer player)
    {
        try { player.Play(); } catch { /* no audio output: ignore */ }
    }

    /// <summary>Two quick sine notes with a soft envelope.</summary>
    private static byte[] Tone(double firstHz, double secondHz)
    {
        const int rate = PcmConverter.TargetSampleRate;
        const int noteSamples = rate * 70 / 1000;
        var pcm = new byte[noteSamples * 2 * 2];
        for (var i = 0; i < noteSamples * 2; i++)
        {
            var hz = i < noteSamples ? firstHz : secondHz;
            var local = i % noteSamples;
            var envelope = Math.Sin(Math.PI * local / noteSamples);
            var sample = Math.Sin(2 * Math.PI * hz * i / rate) * envelope * 0.18;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(sample * short.MaxValue));
        }
        return WavWriter.ToWav(pcm);
    }

    public void Dispose()
    {
        _start.Dispose();
        _stop.Dispose();
    }
}
