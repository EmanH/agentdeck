using System.Media;

namespace AgentDeck;

/// <summary>Subtle UI sounds, synthesised once at startup (no sound files to ship).</summary>
static class Chime
{
    const int Rate = 44100;
    static readonly byte[] DoneWav = Wav(BuildPop(900, 380));
    static readonly byte[] MicOnWav = Wav(BuildPop(320, 820));  // rising pop: mic open
    static readonly byte[] MicOffWav = Wav(BuildPop(820, 320)); // the same pop in reverse: mic closed
    static readonly byte[] PaymentWav = Wav(BuildBells());
    static long _lastPlayed;

    /// <summary>Money in: a soft, rising three-note bell with a little shimmer on top.</summary>
    public static void PlayPayment() => PlayWav(PaymentWav);

    /// <summary>Play the "done" pop, at most once per 1.2 s so several terminals finishing together don't pile up.</summary>
    public static void Play()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPlayed) < 1200) return;
        Interlocked.Exchange(ref _lastPlayed, now);
        PlayWav(DoneWav);
    }

    /// <summary>The mic has started listening.</summary>
    public static void PlayMicOn() => PlayWav(MicOnWav);

    /// <summary>The mic has stopped listening.</summary>
    public static void PlayMicOff() => PlayWav(MicOffWav);

    static void PlayWav(byte[] wav)
    {
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } // async, returns immediately
        catch (Exception ex) { Log.Error("chime", ex); }
    }

    // A soft "pop": one short sine whose pitch glides quickly from fromHz towards toHz, very quiet.
    // Falling sounds like a bubble; rising sounds like a cork/puck popping open.
    static short[] BuildPop(double fromHz, double toHz, double length = 0.09, double volume = 0.12)
    {
        const double sweep = 35; // how fast the pitch glides
        int samples = (int)(Rate * length);
        var pcm = new short[samples];
        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / Rate;
            double freq = toHz + (fromHz - toHz) * Math.Exp(-t * sweep);
            phase += 2 * Math.PI * freq / Rate;
            double envelope = Math.Min(t / 0.002, 1) * Math.Exp(-t * 55); // 2 ms attack, fast decay
            pcm[i] = (short)(Math.Sin(phase) * envelope * volume * short.MaxValue);
        }
        return pcm;
    }

    // Bell notes E6, G#6, B6 90 ms apart, then a quiet E7 sparkle. Each is a sine plus fast-decaying inharmonic
    // partials for a bell colour, with a 4 ms attack and a long soft tail.
    static short[] BuildBells(double volume = 0.11)
    {
        (double hz, double at, double gain)[] notes = [(1318.5, 0, 1), (1661.2, 0.09, 0.9), (1975.5, 0.18, 0.85), (2637.0, 0.30, 0.35)];
        var mix = new double[(int)(Rate * 1.3)];
        foreach (var (hz, at, gain) in notes)
        {
            int start = (int)(at * Rate);
            for (int i = start; i < mix.Length; i++)
            {
                double t = (double)(i - start) / Rate;
                double env = Math.Min(t / 0.004, 1) * Math.Exp(-t * 4.2);
                double tone = Math.Sin(2 * Math.PI * hz * t)
                            + 0.28 * Math.Sin(2 * Math.PI * hz * 2.76 * t) * Math.Exp(-t * 9)
                            + 0.12 * Math.Sin(2 * Math.PI * hz * 5.4 * t) * Math.Exp(-t * 16);
                mix[i] += tone * env * gain;
            }
        }
        var pcm = new short[mix.Length];
        for (int i = 0; i < mix.Length; i++)
        {
            double fadeOut = Math.Min(1, (mix.Length - i) / (Rate * 0.05)); // no click at the end
            pcm[i] = (short)(Math.Clamp(mix[i] * volume * fadeOut, -1, 1) * short.MaxValue);
        }
        return pcm;
    }

    static byte[] Wav(short[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
