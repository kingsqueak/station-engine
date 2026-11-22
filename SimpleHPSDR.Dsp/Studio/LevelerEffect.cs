// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/loudness.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>The ITU-R BS.1770 "K" weighting (a high shelf around 1.7 kHz and
/// a high pass around 38 Hz) for a sample rate: what loudness in LUFS is
/// measured through. Coefficients as in libebur128, valid at any rate.</summary>
internal static class KWeighting
{
    /// <summary>The shelf (run first) and the high pass, for a rate.</summary>
    public static (BiquadCoefficients Shelf, BiquadCoefficients HighPass) ForRate(double sampleRate)
    {
        BiquadCoefficients shelf;
        {
            // The head's acoustic effect: a high shelf of about +4 dB.
            const double f0 = 1681.974450955533;
            const double gain = 3.999843853973347;
            const double q = 0.7071752369554196;
            double kk = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10.0, gain / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + (kk / q) + (kk * kk);
            shelf = new BiquadCoefficients
            {
                B0 = (vh + (vb * kk / q) + (kk * kk)) / a0,
                B1 = 2.0 * ((kk * kk) - vh) / a0,
                B2 = (vh - (vb * kk / q) + (kk * kk)) / a0,
                A1 = 2.0 * ((kk * kk) - 1.0) / a0,
                A2 = (1.0 - (kk / q) + (kk * kk)) / a0,
            };
        }
        BiquadCoefficients highPass;
        {
            // The "RLB" high pass.
            const double f0 = 38.13547087602444;
            const double q = 0.5003270373238773;
            double kk = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1.0 + (kk / q) + (kk * kk);
            highPass = new BiquadCoefficients
            {
                B0 = 1.0,
                B1 = -2.0,
                B2 = 1.0,
                A1 = 2.0 * ((kk * kk) - 1.0) / a0,
                A2 = (1.0 - (kk / q) + (kk * kk)) / a0,
            };
        }
        return (shelf, highPass);
    }

    /// <summary>LUFS from the mean square of the K-weighted signal.</summary>
    public static double LufsFromMeanSquare(double meanSquare) =>
        -0.691 + (10.0 * Math.Log10(Math.Max(meanSquare, 1e-12)));
}

/// <summary>A broadcast leveler: rides the gain slowly towards a target
/// loudness, like a radio station's AGC, so quiet and loud sources come out
/// alike. Loudness is K-weighted (LUFS) over about a second.
///
/// Parameters: "target" (LUFS), "maxBoost" and "maxCut" (dB), "speed" (s: how
/// long a change takes), "freeze" (LUFS: below this the gain holds still, so
/// silence, breaths and room noise aren't turned up). The freeze listens to a
/// fast (25 ms) reading, so the gain stops the moment a source goes quiet,
/// not once the slow reading has caught up; the slow reading holds too, so
/// sound coming back isn't measured against the silence.
///
/// Meters: [gain now (dB, + = boost), loudness now (LUFS)].</summary>
internal sealed class LevelerEffect : StudioEffect
{
    // Parameter positions, as in StudioCatalogue.
    private const int Target = 0;
    private const int MaxBoost = 1;
    private const int MaxCut = 2;
    private const int Speed = 3;
    private const int Freeze = 4;

    // Written on the audio thread, read on the control thread.
    private float _gainMeter;
    private float _loudnessMeter = -70f;

    // Audio thread only.
    private BiquadCoefficients _shelf = BiquadCoefficients.Identity;
    private BiquadCoefficients _highPass = BiquadCoefficients.Identity;
    private BiquadState _shelfState;
    private BiquadState _passState;
    private double _meanSquare;
    private double _window = 1.0; // one-pole step for the ~1 s loudness window
    private double _fastSquare;
    private double _fastWindow = 1.0; // ~25 ms, for the freeze
    private double _gainDb;
    private double _rate = 48000.0;

    public LevelerEffect() : base(StudioCatalogue.Leveler) { }

    public override void Prepare(double sampleRate)
    {
        _rate = sampleRate;
        (_shelf, _highPass) = KWeighting.ForRate(sampleRate);
        _shelfState.Clear();
        _passState.Clear();
        _meanSquare = 0.0;
        _window = 1.0 - Math.Exp(-1.0 / (1.0 * sampleRate)); // ~1 s
        _fastSquare = 0.0;
        _fastWindow = 1.0 - Math.Exp(-1.0 / (0.025 * sampleRate));
        _gainDb = 0.0;
    }

    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 2) return 0;
        into[0] = Volatile.Read(ref _gainMeter);
        into[1] = Volatile.Read(ref _loudnessMeter);
        return 2;
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        double target = p[Target];
        double maxBoost = p[MaxBoost];
        double maxCut = p[MaxCut];
        double freeze = p[Freeze];
        double ride = 1.0 - Math.Exp(-1.0 / (p[Speed] * _rate));

        double meanSquare = _meanSquare;
        double window = _window;
        double fastSquare = _fastSquare;
        double fastWindow = _fastWindow;
        double gainDb = _gainDb;
        double loudness = -70.0;
        for (int i = 0; i < block.Length; i++)
        {
            double x = block[i];
            double y = _passState.Step(in _highPass, _shelfState.Step(in _shelf, x));
            double sum = y * y;
            fastSquare += (sum - fastSquare) * fastWindow;
            // On silence (by the fast reading) everything holds still: the gain,
            // and the slow reading too, so sound coming back isn't measured
            // against the silence and boosted.
            bool active = KWeighting.LufsFromMeanSquare(fastSquare) > freeze;
            if (active)
                meanSquare += (sum - meanSquare) * window;
            loudness = KWeighting.LufsFromMeanSquare(meanSquare);
            if (active)
            {
                double wanted = Math.Clamp(target - loudness, -maxCut, maxBoost);
                gainDb += (wanted - gainDb) * ride;
            }
            float gain = (float)Math.Pow(10.0, gainDb / 20.0);
            block[i] *= gain;
        }
        if (fastSquare < 1e-30) fastSquare = 0.0;
        _meanSquare = meanSquare;
        _fastSquare = fastSquare;
        _gainDb = gainDb;
        _shelfState.Flush();
        _passState.Flush();
        Volatile.Write(ref _gainMeter, (float)gainDb);
        Volatile.Write(ref _loudnessMeter, (float)loudness);
    }
}
