// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/multiband.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A 4-band compressor. Three crossovers ("x1" &lt; "x2" &lt; "x3",
/// Hz) split the signal into four bands with Linkwitz-Riley 24 dB/octave
/// filters, arranged so the bands add back up to the input (flat, only
/// phase-shifted) when nothing is compressing:
/// <code>
///   in ─┬─ LR4 low  @x2 ─ allpass @x3 ─┬─ LR4 low  @x1 ─ band 1
///       │                              └─ LR4 high @x1 ─ band 2
///       └─ LR4 high @x2 ─ allpass @x1 ─┬─ LR4 low  @x3 ─ band 3
///                                      └─ LR4 high @x3 ─ band 4
/// </code>
/// Each band has its own compressor, working on the band's RMS level (10 ms):
/// "b&lt;n&gt;Threshold" (dB), "b&lt;n&gt;Ratio" (n:1), "b&lt;n&gt;Attack" and
/// "b&lt;n&gt;Release" (ms), "b&lt;n&gt;Makeup" (dB), "b&lt;n&gt;On" (off = the
/// band passes uncompressed) and "b&lt;n&gt;Solo" (when any band is soloed,
/// only soloed bands are heard). The knee is soft (6 dB).
/// Crossovers glide; on/off, solo and makeup changes fade (5 ms).
/// Meters: each band's largest gain reduction (dB, positive), then each
/// band's loudest detector level (what the threshold is compared with) as dB
/// above <see cref="MeterFloorDb"/> (0 = at or below it).</summary>
internal sealed class MultibandEffect : StudioEffect
{
    // Parameter positions, as in StudioCatalogue: the three crossovers, then
    // band b's field f at BandBase + b * FieldsPerBand + f.
    private const int CrossoverBase = 0;
    private const int BandBase = 3;
    private const int FieldsPerBand = 7;
    private const int OnField = 0;
    private const int SoloField = 1;
    private const int ThresholdField = 2;
    private const int RatioField = 3;
    private const int AttackField = 4;
    private const int ReleaseField = 5;
    private const int MakeupField = 6;

    private const int BandCount = StudioCatalogue.MultibandBands;
    private const int CrossoverCount = 3;
    private const int BlockFrames = 32;
    public const double MeterFloorDb = -80.0;
    // Crossovers keep at least this ratio apart (about a third of an octave).
    private const double MinSpacing = 1.25;

    // Filter states, in _z: the x2 split (low and high, two stages each), the
    // two allpasses, then the x1 and x3 splits (two stages each, low and high).
    private const int MidLow = 0;
    private const int MidHigh = 2;
    private const int AllpassLow = 4;  // @x3 on the low branch
    private const int AllpassHigh = 5; // @x1 on the high branch
    private const int Band1 = 6;
    private const int Band2 = 8;
    private const int Band3 = 10;
    private const int Band4 = 12;
    private const int StateCount = 14;

    private struct BandState // audio thread only
    {
        public double MeanSquare;  // the level detector
        public double ReductionDb; // smoothed gain reduction
        public double Level;       // smoothed makeup x on x solo, linear
    }

    // Per-band settings for one Process call.
    private struct BandSettings
    {
        public double Threshold;
        public double Ratio;
        public double Attack;  // one-pole coefficients
        public double Release;
        public bool On;
        public double Level;   // target: makeup x solo
    }

    // Audio thread only (and Prepare).
    private readonly double[] _crossNow = new double[CrossoverCount];
    private readonly BiquadCoefficients[] _low = new BiquadCoefficients[CrossoverCount];
    private readonly BiquadCoefficients[] _high = new BiquadCoefficients[CrossoverCount];
    private readonly BiquadCoefficients[] _allpass = new BiquadCoefficients[CrossoverCount];
    private bool _fresh = true;
    private readonly BiquadState[] _z = new BiquadState[StateCount];
    private readonly BandState[] _state = new BandState[BandCount];
    private double _rate = 48000.0;
    private double _glide = 1.0;
    private double _levelStep = 1.0;  // one-pole coefficient per sample, ~5 ms
    private double _rmsStep = 1.0;    // the level detector's, ~10 ms
    private double _offRelease = 0.0; // a band turned off lets go of its reduction in ~5 ms

    private readonly HeldMeter[] _reductionMeter = new HeldMeter[BandCount];
    private readonly HeldMeter[] _levelMeter = new HeldMeter[BandCount]; // dB above MeterFloorDb

    public MultibandEffect() : base(StudioCatalogue.Multiband) { }

    public override void Prepare(double sampleRate)
    {
        var p = Params;
        _rate = sampleRate;
        _glide = 1.0 - Math.Exp(-(double)BlockFrames / (0.020 * sampleRate));
        _levelStep = 1.0 - Math.Exp(-1.0 / (0.005 * sampleRate));
        _rmsStep = 1.0 - Math.Exp(-1.0 / (0.010 * sampleRate));
        _offRelease = Math.Exp(-1.0 / (0.005 * sampleRate));
        for (int i = 0; i < CrossoverCount; i++) _crossNow[i] = p[CrossoverBase + i];
        _fresh = true;
        for (int b = 0; b < BandCount; b++)
        {
            int at = BandBase + (b * FieldsPerBand);
            ref BandState s = ref _state[b];
            s.MeanSquare = 0.0;
            s.ReductionDb = 0.0;
            s.Level = p[at + OnField] >= 0.5 ? Math.Pow(10.0, p[at + MakeupField] / 20.0) : 1.0;
        }
    }

    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 2 * BandCount) return 0;
        for (int b = 0; b < BandCount; b++)
        {
            into[b] = _reductionMeter[b].Take();
            into[BandCount + b] = _levelMeter[b].Take();
        }
        return 2 * BandCount;
    }

    /// <summary>Back from bypass: filter memory and gain reduction are from before.</summary>
    public override void Resume()
    {
        for (int i = 0; i < StateCount; i++) _z[i].Clear();
        for (int b = 0; b < BandCount; b++)
        {
            _state[b].MeanSquare = 0.0;
            _state[b].ReductionDb = 0.0;
        }
    }

    private void UpdateCrossovers(double[] p, double glide)
    {
        bool moved = _fresh;
        for (int i = 0; i < CrossoverCount; i++)
        {
            double target = p[CrossoverBase + i];
            double now = _crossNow[i];
            if (Math.Abs(now - target) > now * 1e-4)
            {
                now = Math.Exp(Math.Log(now) + ((Math.Log(target) - Math.Log(now)) * glide));
                moved = true;
            }
            else if (now != target)
            {
                now = target;
                moved = true;
            }
            _crossNow[i] = now;
        }
        if (!moved) return;
        _fresh = false;
        // Kept in order and apart, and clear of Nyquist.
        double top = 0.45 * _rate;
        double f0 = Math.Min(_crossNow[0], top / (MinSpacing * MinSpacing));
        double f1 = Math.Min(Math.Max(_crossNow[1], f0 * MinSpacing), top / MinSpacing);
        double f2 = Math.Min(Math.Max(_crossNow[2], f1 * MinSpacing), top);
        SetCrossover(0, f0);
        SetCrossover(1, f1);
        SetCrossover(2, f2);
    }

    private void SetCrossover(int i, double freq)
    {
        _low[i] = Biquad.Design(BiquadType.HighCut, freq, 0.0, Biquad.ButterworthQ, _rate);
        _high[i] = Biquad.Design(BiquadType.LowCut, freq, 0.0, Biquad.ButterworthQ, _rate);
        _allpass[i] = Biquad.Allpass(freq, _rate);
    }

    // A Linkwitz-Riley 24 dB/octave filter: the same Butterworth biquad twice.
    private static double Lr4(in BiquadCoefficients c, ref BiquadState first, ref BiquadState second,
        double x) => second.Step(in c, first.Step(in c, x));

    public override void Process(Span<float> block)
    {
        var p = Params;

        Span<BandSettings> bands = stackalloc BandSettings[BandCount];
        // Each solo read once, so a change mid-call can't leave every band unheard.
        bool anySolo = false;
        for (int b = 0; b < BandCount; b++)
            if (p[BandBase + (b * FieldsPerBand) + SoloField] >= 0.5) anySolo = true;
        for (int b = 0; b < BandCount; b++)
        {
            int at = BandBase + (b * FieldsPerBand);
            bool on = p[at + OnField] >= 0.5;
            bool heard = !anySolo || p[at + SoloField] >= 0.5;
            double level = 0.0; // a band not heard (another is soloed)
            if (heard) level = on ? Math.Pow(10.0, p[at + MakeupField] / 20.0) : 1.0;
            bands[b] = new BandSettings
            {
                Threshold = p[at + ThresholdField],
                Ratio = p[at + RatioField],
                Attack = Math.Exp(-1.0 / (p[at + AttackField] * 0.001 * _rate)),
                Release = Math.Exp(-1.0 / (p[at + ReleaseField] * 0.001 * _rate)),
                On = on,
                Level = level,
            };
        }

        Span<float> peakReduction = stackalloc float[BandCount];
        Span<double> peakSquare = stackalloc double[BandCount]; // the detectors' loudest
        Span<double> split = stackalloc double[BandCount];
        peakReduction.Clear();
        peakSquare.Clear();
        Span<BiquadState> z = _z;
        Span<BandState> state = _state;
        double rmsStep = _rmsStep, levelStep = _levelStep, offRelease = _offRelease;
        int frames = block.Length;
        for (int start = 0; start < frames; start += BlockFrames)
        {
            int end = Math.Min(frames, start + BlockFrames);
            // Glides keep their 20 ms however short the block (or the quantum) is.
            UpdateCrossovers(p, end - start == BlockFrames
                ? _glide
                : 1.0 - Math.Exp(-(double)(end - start) / (0.020 * _rate)));
            BiquadCoefficients low0 = _low[0], high0 = _high[0], low1 = _low[1], high1 = _high[1];
            BiquadCoefficients low2 = _low[2], high2 = _high[2], allpass0 = _allpass[0], allpass2 = _allpass[2];
            for (int i = start; i < end; i++)
            {
                // Split into the four bands.
                double x = block[i];
                // The low branch gets the allpass of x3, the high branch that
                // of x1, so all four bands come out with the same phase.
                double low = z[AllpassLow].Step(in allpass2,
                    Lr4(in low1, ref z[MidLow], ref z[MidLow + 1], x));
                double high = z[AllpassHigh].Step(in allpass0,
                    Lr4(in high1, ref z[MidHigh], ref z[MidHigh + 1], x));
                split[0] = Lr4(in low0, ref z[Band1], ref z[Band1 + 1], low);
                split[1] = Lr4(in high0, ref z[Band2], ref z[Band2 + 1], low);
                split[2] = Lr4(in low2, ref z[Band3], ref z[Band3 + 1], high);
                split[3] = Lr4(in high2, ref z[Band4], ref z[Band4 + 1], high);
                // Each band's gain, from its level.
                double sum = 0.0;
                for (int b = 0; b < BandCount; b++)
                {
                    ref readonly BandSettings band = ref bands[b];
                    ref BandState s = ref state[b];
                    double peak = Math.Abs(split[b]);
                    s.MeanSquare += ((peak * peak) - s.MeanSquare) * rmsStep;
                    double wanted = 0.0;
                    if (band.On)
                    {
                        double level = 10.0 * Math.Log10(Math.Max(s.MeanSquare, 1e-18));
                        wanted = level - StudioMath.CompressorCurve(level, band.Threshold, band.Ratio);
                    }
                    double k = wanted > s.ReductionDb ? band.Attack : band.Release;
                    if (!band.On) k = offRelease; // off means uncompressed, now
                    s.ReductionDb = wanted + ((s.ReductionDb - wanted) * k);
                    s.Level += (band.Level - s.Level) * levelStep;
                    peakReduction[b] = Math.Max(peakReduction[b], (float)s.ReductionDb);
                    peakSquare[b] = Math.Max(peakSquare[b], s.MeanSquare);
                    sum += split[b] * (s.Level * Math.Pow(10.0, -s.ReductionDb / 20.0));
                }
                block[i] = (float)sum;
            }
        }
        for (int i = 0; i < StateCount; i++) z[i].Flush();
        // Envelopes out of denormals in silence (and a soloed-away band's level).
        for (int b = 0; b < BandCount; b++)
        {
            ref BandState s = ref state[b];
            if (s.MeanSquare < 1e-30) s.MeanSquare = 0.0;
            if (Math.Abs(s.ReductionDb) < 1e-9) s.ReductionDb = 0.0;
            if (s.Level < 1e-9) s.Level = 0.0;
        }
        for (int b = 0; b < BandCount; b++)
        {
            _reductionMeter[b].Raise(peakReduction[b]);
            float above = (float)Math.Max(
                0.0, (10.0 * Math.Log10(Math.Max(peakSquare[b], 1e-18))) - MeterFloorDb);
            _levelMeter[b].Raise(above);
        }
    }
}
