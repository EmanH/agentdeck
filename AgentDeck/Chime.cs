using System.Media;

namespace AgentDeck;

/// <summary>A subtle "done" pop, synthesised once at startup (no sound file to ship).</summary>
static class Chime
{
    const int Rate = 44100;
    static readonly byte[] Wav = Build();
    static long _lastPlayed;

    /// <summary>Play the chime, at most once per 1.2 s so several terminals finishing together don't pile up.</summary>
    public static void Play()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPlayed) < 1200) return;
        Interlocked.Exchange(ref _lastPlayed, now);
        try { new SoundPlayer(new MemoryStream(Wav)).Play(); } // async, returns immediately
        catch (Exception ex) { Log.Error("chime", ex); }
    }

    static byte[] Build()
    {
        // A soft "pop": one short sine whose pitch drops quickly (like a bubble), very quiet.
        const double length = 0.09, volume = 0.12;
        const double startHz = 900, endHz = 380, sweep = 35; // pitch falls from startHz towards endHz
        int samples = (int)(Rate * length);
        var pcm = new short[samples];
        double phase = 0;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / Rate;
            double freq = endHz + (startHz - endHz) * Math.Exp(-t * sweep);
            phase += 2 * Math.PI * freq / Rate;
            double envelope = Math.Min(t / 0.002, 1) * Math.Exp(-t * 55); // 2 ms attack, fast decay
            pcm[i] = (short)(Math.Sin(phase) * envelope * volume * short.MaxValue);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
