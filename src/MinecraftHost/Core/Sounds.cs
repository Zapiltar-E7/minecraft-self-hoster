using System.Media;

namespace MinecraftHost.Core;

/// <summary>
/// Short notification sounds, made in memory (no sound files): a light rising jingle for "done / online"
/// and a soft falling two-note tone for "failed / crashed". Switched off with Settings > Play sounds.
/// </summary>
public static class Sounds
{
    const int Rate = 44100;

    // (frequency Hz, start s, length s)
    static readonly Lazy<byte[]> SuccessWav = new(() => Render(new[]
    {
        (523.25, 0.00, 0.22),   // C5
        (659.25, 0.09, 0.22),   // E5
        (783.99, 0.18, 0.22),   // G5
        (1046.5, 0.27, 0.40),   // C6
    }, volume: 0.16, bright: 0.25));

    static readonly Lazy<byte[]> ErrorWav = new(() => Render(new[]
    {
        (466.16, 0.00, 0.20),   // A#4
        (349.23, 0.16, 0.34),   // F4
    }, volume: 0.17, bright: 0.45));

    public static void Success() => Play(SuccessWav.Value);
    public static void Error() => Play(ErrorWav.Value);

    // the creeper easter egg: a hissing fuse, then a boom (both made here, not Minecraft's own sounds)
    public const double FuseSeconds = 1.5;
    static readonly Lazy<byte[]> FuseWav = new(() => ToWav(Fuse()));
    static readonly Lazy<byte[]> BoomWav = new(() => ToWav(Boom()));
    public static void CreeperFuse() => Play(FuseWav.Value);
    public static void CreeperBoom() => Play(BoomWav.Value);
    /// <summary>Plays a ready-made WAV (Minecraft's own creeper sounds, read from the game on this PC).</summary>
    public static void PlayWav(byte[] wav) => Play(wav);

    /// <summary>A sizzling hiss that swells, with little crackles: bright noise (white noise minus its smoothed self).</summary>
    static double[] Fuse()
    {
        var rng = new Random(7);
        var samples = new double[(int)(FuseSeconds * Rate)];
        double low = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            var noise = rng.NextDouble() * 2 - 1;
            low += (noise - low) * 0.35;
            var hiss = noise - low;                                   // high-passed: hiss, not rumble
            var crackle = rng.NextDouble() < 0.0015 ? (rng.NextDouble() * 2 - 1) * 2.5 : 0;
            var swell = 0.35 + 0.65 * t / FuseSeconds;
            var flutter = 0.8 + 0.2 * Math.Sin(2 * Math.PI * 9 * t);  // the fuse sputters a little
            var edges = Math.Min(1, t / 0.03) * Math.Min(1, (FuseSeconds - t) / 0.02);
            samples[i] = (hiss * flutter + crackle) * swell * edges * 0.11;
        }
        return samples;
    }

    /// <summary>A deep thump under a roar of rumbling noise that dies away over about a second and a half.</summary>
    static double[] Boom()
    {
        var rng = new Random(11);
        const double length = 1.6;
        var samples = new double[(int)(length * Rate)];
        double brown = 0, smooth = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;
            brown = (brown + (rng.NextDouble() * 2 - 1) * 0.12) * 0.985;   // brown noise: the rumble
            smooth += (brown - smooth) * (0.08 + 0.5 * Math.Exp(-t * 6));  // starts bright, darkens as it fades
            var crack = (rng.NextDouble() * 2 - 1) * Math.Exp(-t * 40);     // the sharp first instant
            var thump = Math.Sin(2 * Math.PI * (55 - 20 * t) * t) * Math.Exp(-t * 4.5);
            var decay = Math.Exp(-t * 2.6) * Math.Min(1, t / 0.004) * Math.Min(1, (length - t) / 0.05);
            samples[i] = (smooth * 3.2 + crack * 0.5 + thump * 0.55) * decay * 0.16;
        }
        return samples;
    }

    static void Play(byte[] wav)
    {
        if (HostConfig.Load()?.PlaySounds == false) return;
        try { new SoundPlayer(new MemoryStream(wav)).Play(); }   // plays in the background
        catch { }   // no audio device: just stay quiet
    }

    /// <summary>Soft sine notes with a gentle overtone, quick attack and smooth fade, as a 16-bit mono WAV.</summary>
    static byte[] Render((double Freq, double Start, double Length)[] notes, double volume, double bright)
    {
        var total = notes.Max(n => n.Start + n.Length) + 0.05;
        var samples = new double[(int)(total * Rate)];
        foreach (var (freq, start, length) in notes)
        {
            var from = (int)(start * Rate);
            var count = (int)(length * Rate);
            for (var i = 0; i < count && from + i < samples.Length; i++)
            {
                var t = (double)i / Rate;
                var attack = Math.Min(1, t / 0.008);                  // no click at the start
                var release = Math.Exp(-4.5 * t / length);            // fades like a bell
                var tail = Math.Min(1, (length - t) / 0.02);          // no click at the end
                var wave = Math.Sin(2 * Math.PI * freq * t) + bright * Math.Sin(4 * Math.PI * freq * t);
                samples[from + i] += wave / (1 + bright) * attack * release * tail * volume;
            }
        }

        return ToWav(samples);
    }

    static byte[] ToWav(double[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write((short)(Math.Clamp(s, -1, 1) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
