namespace EntraGuard.Shared.Voice;

/// <summary>
/// Converts ACS call audio to what the speaker model expects.
///
/// ACS streams 24 kHz PCM16 mono; ECAPA-TDNN requires 16 kHz. That is a 3:2 ratio, so every
/// three input samples become two output samples — no fractional phase accumulator, no
/// drift over a long call.
///
/// Getting this wrong does not throw. It produces audio that is subtly the wrong speed,
/// which shifts formants and makes a genuine user score like an impostor — a failure that
/// looks like the biometrics being inaccurate rather than like a bug.
/// </summary>
public static class AudioResampler
{
    public const int AcsSampleRate = 24000;
    public const int ModelSampleRate = 16000;

    /// <summary>
    /// Downsample 24 kHz PCM16 to 16 kHz PCM16.
    /// </summary>
    /// <remarks>
    /// Averages each group of three input samples into two outputs rather than dropping
    /// one. Naive decimation aliases everything above 8 kHz back down into the speech band,
    /// and sibilants land squarely there — exactly the part of a voice the model relies on.
    /// Averaging is a crude low-pass, but a crude one applied is worth more than a perfect
    /// one skipped.
    /// </remarks>
    public static byte[] Downsample24To16(ReadOnlySpan<byte> pcm24)
    {
        // Whole samples only; a trailing odd byte is half a sample and means nothing.
        var inputSamples = pcm24.Length / 2;

        // Work in whole 3-sample groups so the ratio stays exact.
        var groups = inputSamples / 3;
        if (groups == 0)
        {
            return [];
        }

        var output = new byte[groups * 2 * 2];

        for (var g = 0; g < groups; g++)
        {
            var i = g * 3 * 2;

            var s0 = BitConverter.ToInt16(pcm24[i..]);
            var s1 = BitConverter.ToInt16(pcm24[(i + 2)..]);
            var s2 = BitConverter.ToInt16(pcm24[(i + 4)..]);

            // Two outputs weighted toward the samples they sit between.
            var o0 = (short)((2 * s0 + s1) / 3);
            var o1 = (short)((s1 + 2 * s2) / 3);

            var o = g * 4;
            BitConverter.TryWriteBytes(output.AsSpan(o), o0);
            BitConverter.TryWriteBytes(output.AsSpan(o + 2), o1);
        }

        return output;
    }

    /// <summary>How many seconds of 16 kHz PCM16 this many bytes represents.</summary>
    public static double Seconds(int pcm16ByteCount) =>
        pcm16ByteCount / 2.0 / ModelSampleRate;

    /// <summary>
    /// Rough voiced-energy ratio, used to reject buffers that are mostly line noise.
    /// </summary>
    /// <remarks>
    /// Not a real VAD. It exists to catch the case where a caller says nothing and the
    /// buffer fills with hiss — which would otherwise produce a confident embedding of a
    /// quiet room and score it against a human.
    /// </remarks>
    public static double VoicedRatio(ReadOnlySpan<byte> pcm16, short threshold = 500)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0)
        {
            return 0;
        }

        var loud = 0;
        for (var i = 0; i < samples; i++)
        {
            var s = BitConverter.ToInt16(pcm16[(i * 2)..]);
            if (Math.Abs((int)s) >= threshold)
            {
                loud++;
            }
        }

        return (double)loud / samples;
    }
}
