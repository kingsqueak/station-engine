// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/eq.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A 6-band parametric EQ: each band a biquad (RBJ "Audio EQ
/// Cookbook") that is a bell, a low or high shelf, or a 12 dB/octave low or
/// high cut. Parameters per band n (1-6): "b&lt;n&gt;On", "b&lt;n&gt;Type" (a
/// <see cref="BiquadType"/>), "b&lt;n&gt;Freq" (Hz), "b&lt;n&gt;Gain" (dB; bells
/// and shelves), "b&lt;n&gt;Q".
/// Frequency, gain and Q glide to new values (about 20 ms) instead of jumping;
/// switching a band on or off, or changing its type, fades the band out and
/// back in. All of that happens on the audio thread without allocating:
/// coefficients are recomputed there, every 32 samples while a band moves.</summary>
internal sealed class ParametricEqEffect : StudioEffect
{
    // Parameter positions, as in StudioCatalogue: band b's field f is at
    // b * FieldsPerBand + f.
    private const int FieldsPerBand = 5;
    private const int OnField = 0;
    private const int TypeField = 1;
    private const int FreqField = 2;
    private const int GainField = 3;
    private const int QField = 4;

    private const int BandCount = StudioCatalogue.EqBands;
    private const int BlockFrames = 32; // coefficient update interval while moving

    // Audio thread only (and Prepare).
    private struct BandState
    {
        // The parameters the coefficients were made from.
        public BiquadType Type;
        public double Freq;
        public double GainDb;
        public double Q;
        public float Mix;   // 0 = band out, 1 = in
        public bool Fresh;  // coefficients need computing
        public BiquadCoefficients C;
        public BiquadState Z;
    }

    private readonly BandState[] _state = new BandState[BandCount];
    private double _rate = 48000.0;
    private double _glide = 1.0;    // per full block, one-pole
    private float _fadeStep = 1f;   // per sample, for on/off and type changes

    public ParametricEqEffect() : base(StudioCatalogue.Eq) { }

    public override void Prepare(double sampleRate)
    {
        var p = Params;
        _rate = sampleRate;
        // Glide time constant about 20 ms, applied once per block.
        _glide = 1.0 - Math.Exp(-(double)BlockFrames / (0.020 * sampleRate));
        _fadeStep = (float)(1.0 / Math.Max(1.0, 0.010 * sampleRate)); // 10 ms
        // Start where the parameters are, with no glide.
        for (int b = 0; b < BandCount; b++)
        {
            int at = b * FieldsPerBand;
            ref BandState s = ref _state[b];
            s.Type = (BiquadType)(int)p[at + TypeField];
            s.Freq = p[at + FreqField];
            s.GainDb = p[at + GainField];
            s.Q = p[at + QField];
            s.Mix = p[at + OnField] >= 0.5 ? 1f : 0f;
            s.Fresh = true;
            s.Z.Clear();
        }
    }

    /// <summary>Back from bypass: the bands' filter memory is from before; drop it.</summary>
    public override void Resume()
    {
        for (int b = 0; b < BandCount; b++) _state[b].Z.Clear();
    }

    private static bool Near(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    private void UpdateBand(ref BandState s, double[] p, int at, double blockGlide)
    {
        // Glide frequency (on a log scale), gain and Q towards their targets.
        double freq = p[at + FreqField];
        double gain = p[at + GainField];
        double q = p[at + QField];
        bool moved = false;
        if (!Near(s.Freq, freq, s.Freq * 1e-4))
        {
            s.Freq = Math.Exp(Math.Log(s.Freq) + ((Math.Log(freq) - Math.Log(s.Freq)) * blockGlide));
            moved = true;
        }
        else
        {
            s.Freq = freq;
        }
        if (!Near(s.GainDb, gain, 1e-3))
        {
            s.GainDb += (gain - s.GainDb) * blockGlide;
            moved = true;
        }
        else
        {
            s.GainDb = gain;
        }
        if (!Near(s.Q, q, 1e-4))
        {
            s.Q = Math.Exp(Math.Log(s.Q) + ((Math.Log(q) - Math.Log(s.Q)) * blockGlide));
            moved = true;
        }
        else
        {
            s.Q = q;
        }
        // A type change waits until the band has faded out (see Process).
        var type = (BiquadType)(int)p[at + TypeField];
        if (type != s.Type && s.Mix == 0f)
        {
            s.Type = type;
            s.Z.Clear();
            moved = true;
        }
        if (moved || s.Fresh)
        {
            s.C = Biquad.Design(s.Type, s.Freq, s.GainDb, s.Q, _rate);
            s.Fresh = false;
        }
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        int frames = block.Length;
        float fadeStep = _fadeStep;
        for (int start = 0; start < frames; start += BlockFrames)
        {
            int n = Math.Min(BlockFrames, frames - start);
            // Glides keep their 20 ms however short the block (or the quantum) is.
            double blockGlide = n == BlockFrames
                ? _glide
                : 1.0 - Math.Exp(-(double)n / (0.020 * _rate));
            for (int b = 0; b < BandCount; b++)
            {
                int at = b * FieldsPerBand;
                ref BandState s = ref _state[b];
                UpdateBand(ref s, p, at, blockGlide);
                var type = (BiquadType)(int)p[at + TypeField];
                // In while on and of the wanted type; out to switch type.
                float target = p[at + OnField] >= 0.5 && type == s.Type ? 1f : 0f;
                if (s.Mix == 0f && target == 0f) continue; // out: not processed

                BiquadCoefficients c = s.C;
                BiquadState z = s.Z;
                float mix = s.Mix;
                for (int i = start; i < start + n; i++)
                {
                    mix = target > mix ? Math.Min(target, mix + fadeStep)
                                       : Math.Max(target, mix - fadeStep);
                    double x = block[i];
                    double y = z.Step(in c, x);
                    block[i] = (float)(x + ((y - x) * mix));
                }
                s.Mix = mix;
                if (mix == 0f) z.Clear(); // faded out: start clean when it comes back
                // No denormals in the filter state once the input goes quiet.
                z.Flush();
                s.Z = z;
            }
        }
    }
}
