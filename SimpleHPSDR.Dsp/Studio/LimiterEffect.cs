// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/loudness.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A safety limiter: no sample leaves above the "ceiling" (dBFS). It
/// looks 1.5 ms ahead (that much latency, plus the true-peak detector's few
/// samples), so the gain is already down when a peak arrives, then recovers
/// over "release" (ms). With "truePeak" on, the ceiling is in dBTP: peaks
/// between samples (as a DAC or a lossy encoder makes them) are held under it
/// too.
///
/// Meters: [gain reduction (dB, positive)], the largest since the last read.</summary>
internal sealed class LimiterEffect : StudioEffect
{
    public const double LookaheadSeconds = 0.0015;
    public const int MaxLookahead = 1024; // samples: 1.5 ms up to 384 kHz and then some

    // Parameter positions, as in StudioCatalogue.
    private const int Ceiling = 0;
    private const int Release = 1;
    private const int TruePeak = 2;

    private HeldMeter _meter;

    // Audio thread only.
    private readonly TruePeakDetector _detector = new();
    private readonly float[] _delay = new float[MaxLookahead];
    private readonly double[] _needed = new double[MaxLookahead];   // gain each sample needs
    private readonly double[] _smoothed = new double[MaxLookahead]; // released gain, for the average
    private int _length = 1;  // lookahead in samples
    private int _at;          // ring position
    private double _min = 1.0; // lowest needed gain in the window
    private int _minAge;      // samples since _min entered
    private double _released = 1.0;
    private double _sum;      // of _smoothed over the window
    private double _rate = 48000.0;

    public LimiterEffect() : base(StudioCatalogue.Limiter) { }

    /// <summary>The lookahead, in both modes.</summary>
    public override int Latency => _length - 1;

    public override void Prepare(double sampleRate)
    {
        _rate = sampleRate;
        // The true-peak detector's delay is looked ahead too, so its readings
        // (a few samples late) still come before their samples leave.
        _length = Math.Clamp(
            (int)Math.Round(LookaheadSeconds * sampleRate, MidpointRounding.AwayFromZero) + 1
                + TruePeakDetector.Delay,
            1, MaxLookahead);
        Resume();
    }

    public override void Resume()
    {
        _at = 0;
        Array.Clear(_delay);
        Array.Fill(_needed, 1.0);
        Array.Fill(_smoothed, 1.0);
        _min = 1.0;
        _minAge = 0;
        _released = 1.0;
        _sum = _length;
        _detector.Reset();
    }

    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 1) return 0;
        into[0] = _meter.Take();
        return 1;
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        double ceiling = Math.Pow(10.0, p[Ceiling] / 20.0);
        float ceilingF = (float)ceiling;
        double release = Math.Exp(-1.0 / (p[Release] * 0.001 * _rate));
        bool truePeak = p[TruePeak] >= 0.5;
        int length = _length;
        float[] delay = _delay;
        double[] needed = _needed;
        double[] smoothed = _smoothed;
        TruePeakDetector detector = _detector;
        int atNow = _at;
        double min = _min;
        int minAge = _minAge;
        double released = _released;
        double sum = _sum;
        double deepest = 1.0;

        // Each sample's needed gain is held as the minimum over the lookahead
        // window, released smoothly, then averaged over the same window. A peak
        // leaves the delay line (length - 1 samples late) exactly when that
        // average has come all the way down for it.
        for (int i = 0; i < block.Length; i++)
        {
            int at = atNow;
            float x = block[i];
            // The detector runs either way, so switching it on finds it settled.
            float between = detector.Process(x);
            double peak = truePeak ? Math.Max(between, Math.Abs(x)) : Math.Abs(x);
            double need = peak > ceiling ? ceiling / peak : 1.0;
            needed[at] = need;
            if (need <= min)
            {
                min = need;
                minAge = 0;
            }
            else if (++minAge >= length)
            {
                // The minimum left the window: find the next one.
                min = 1.0;
                minAge = 0;
                for (int back = 0; back < length; back++)
                {
                    double v = needed[(at + length - back) % length];
                    if (v < min)
                    {
                        min = v;
                        minAge = back;
                    }
                }
            }
            released = min < released ? min : min + ((released - min) * release);
            sum += released - smoothed[at];
            smoothed[at] = released;
            double gain = sum / length;
            deepest = Math.Min(deepest, gain);

            int outAt = (at + 1) % length; // written length - 1 samples ago
            delay[at] = x;
            float y = delay[outAt] * (float)gain;
            block[i] = Math.Clamp(y, -ceilingF, ceilingF); // rounding can't get past
            if (++atNow == length)
            {
                atNow = 0;
                // Recount the sum now and then, so rounding can't build up.
                sum = 0.0;
                for (int k = 0; k < length; k++)
                    sum += smoothed[k];
            }
        }
        _at = atNow;
        _min = min;
        _minAge = minAge;
        _released = released;
        _sum = sum;
        _meter.Raise((float)(-20.0 * Math.Log10(Math.Max(deepest, 1e-6))));
    }
}
