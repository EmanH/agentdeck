using System.Media;

namespace AgentDeck;

/// <summary>Subtle UI sounds, synthesised once at startup (no sound files to ship).</summary>
static class Chime
{
    const int Rate = 44100;
    static readonly byte[] DoneWav = Wav(BuildPop(900, 380));
    static readonly byte[] MicOnWav = Wav(BuildPop(320, 820));  // rising pop: mic open
    static readonly byte[] MicOffWav = Wav(BuildPop(820, 320)); // the same pop in reverse: mic closed
    static long _lastPlayed;

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
