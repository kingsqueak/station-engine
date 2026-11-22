// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/voice.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A single-band compressor for speech. "amount" (0-100 %) sets
/// everything at once: threshold from -10 to -35 dB, ratio from 1:1 to 6:1,
/// 5 ms attack, 120 ms release and makeup gain to match, so 0 % is no change
/// at all. With "advanced" on (labelled Full controls), "threshold" (dB),
/// "ratio" (n:1), "attack", "release" (ms) and "makeup" (dB) are used
/// instead. The knee is soft (6 dB).</summary>
internal sealed class CompressorEffect : StudioEffect
{
    /// <summary>A full set of controls (dB, n:1, ms, ms, dB).</summary>
    public readonly record struct Settings(double Threshold, double Ratio, double Attack, double Release,
        double Makeup);

    // Parameter positions, as in StudioCatalogue.
    private const int Amount = 0;
    private const int Advanced = 1;
    private const int ThresholdParam = 2;
    private const int RatioParam = 3;
    private const int AttackParam = 4;
    private const int ReleaseParam = 5;
    private const int MakeupParam = 6;

    private HeldMeter _meter; // gain reduction (dB, positive), the largest since the last read
    private float _inUse = -20f; // the threshold in use (dB)

    // Audio thread only.
    private double _rate = 48000.0;
    private double _envelope;
    private double _envelopeFall; // ~10 ms
    private double _reductionDb;
    private double _makeupNow = 1.0; // linear, glides
    private double _glide = 1.0; // per sample, ~20 ms

    public CompressorEffect() : base(StudioCatalogue.Compressor) { }

    /// <summary>What the knob stands for at amount (0-100 %).</summary>
    public static Settings FromAmount(double amount)
    {
        double a = Math.Clamp(amount, 0.0, 100.0) / 100.0;
        double threshold = -10.0 - (25.0 * a);
        double ratio = 1.0 + (5.0 * a);
        // Half of what a full-scale peak loses: speech comes back up about as
        // loud as before, only steadier.
        double makeup = 0.5 * -threshold * (1.0 - (1.0 / ratio));
        return new Settings(threshold, ratio, 5.0, 120.0, makeup);
    }

    public override void Prepare(double sampleRate)
    {
        var p = Params;
        _rate = sampleRate;
        _glide = 1.0 - StudioMath.Coefficient(20.0, sampleRate);
        _envelopeFall = StudioMath.Coefficient(10.0, sampleRate);
        _envelope = 0.0;
        _reductionDb = 0.0;
        double makeup = p[Advanced] >= 0.5 ? p[MakeupParam] : FromAmount(p[Amount]).Makeup;
        _makeupNow = Math.Pow(10.0, makeup / 20.0);
    }

    public override void Resume()
    {
        _envelope = 0.0;
        _reductionDb = 0.0;
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        Settings s = p[Advanced] >= 0.5
            ? new Settings(p[ThresholdParam], p[RatioParam], p[AttackParam], p[ReleaseParam], p[MakeupParam])
            : FromAmount(p[Amount]);
        Volatile.Write(ref _inUse, (float)s.Threshold);
        double attack = StudioMath.Coefficient(s.Attack, _rate);
        double release = StudioMath.Coefficient(s.Release, _rate);
        double makeup = Math.Pow(10.0, s.Makeup / 20.0);
        double threshold = s.Threshold;
        double ratio = s.Ratio;
        double envelope = _envelope;
        double envelopeFall = _envelopeFall;
        double reductionDb = _reductionDb;
        double makeupNow = _makeupNow;
        double glide = _glide;
        double most = 0.0;
        for (int i = 0; i < block.Length; i++)
        {
            // The level holds each peak and falls away over ~10 ms, so the
            // dips inside every cycle don't read as quiet.
            envelope = Math.Max(Math.Abs((double)block[i]), envelope * envelopeFall);
            double level = StudioMath.ToDb(envelope);
            double wanted = level - StudioMath.CompressorCurve(level, threshold, ratio);
            double k = wanted > reductionDb ? attack : release;
            reductionDb = wanted + ((reductionDb - wanted) * k);
            makeupNow += (makeup - makeupNow) * glide;
            float gain = (float)(Math.Pow(10.0, -reductionDb / 20.0) * makeupNow);
            block[i] *= gain;
            most = Math.Max(most, reductionDb);
        }
        if (envelope < 1e-20)
        {
            envelope = 0.0; // out of denormals in silence
        }
        _envelope = envelope;
        _reductionDb = reductionDb;
        _makeupNow = makeupNow;
        _meter.Raise((float)most);
    }

    /// <summary>[gain reduction (dB, positive, the largest since the last
    /// read), the threshold in use (dB)].</summary>
    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 2) return 0;
        into[0] = _meter.Take();
        into[1] = Volatile.Read(ref _inUse);
        return 2;
    }
}
