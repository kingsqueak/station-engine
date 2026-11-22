// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/voice.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A downward expander: below "threshold" (dB) the sound is turned
/// down "ratio" times as fast as it falls (1:2 means 10 dB under comes out
/// 20 dB under), by at most "range" dB. Gentler than a gate on breaths and
/// room sound between words. "attack" (opening) and "release" (closing) in
/// ms.</summary>
internal sealed class ExpanderEffect : StudioEffect
{
    // Parameter positions, as in StudioCatalogue.
    private const int Threshold = 0;
    private const int Ratio = 1;
    private const int Range = 2;
    private const int Attack = 3;
    private const int Release = 4;

    private HeldMeter _meter; // the cut (dB, positive), the largest since the last read

    // Audio thread only.
    private double _rate = 48000.0;
    private double _envelope;
    private double _envelopeFall; // ~20 ms
    private double _cutDb;

    public ExpanderEffect() : base(StudioCatalogue.Expander) { }

    public override void Prepare(double sampleRate)
    {
        _rate = sampleRate;
        _envelope = 0.0;
        _envelopeFall = StudioMath.Coefficient(20.0, sampleRate);
        _cutDb = Params[Range]; // starts closed, so nothing pops through
    }

    public override void Resume()
    {
        _envelope = 0.0;
        _cutDb = Params[Range]; // closed again, as when new
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        double threshold = p[Threshold];
        double ratio = p[Ratio];
        double range = p[Range];
        double attack = StudioMath.Coefficient(p[Attack], _rate);
        double release = StudioMath.Coefficient(p[Release], _rate);
        double envelope = _envelope;
        double envelopeFall = _envelopeFall;
        double cutDb = _cutDb;
        double most = 0.0;
        for (int i = 0; i < block.Length; i++)
        {
            envelope = Math.Max(Math.Abs((double)block[i]), envelope * envelopeFall);
            double under = threshold - StudioMath.ToDb(envelope);
            double wanted = Math.Clamp(under * (ratio - 1.0), 0.0, range);
            // Less cut is opening (attack); more is closing (release).
            double k = wanted < cutDb ? attack : release;
            cutDb = wanted + ((cutDb - wanted) * k);
            float gain = (float)Math.Pow(10.0, -cutDb / 20.0);
            block[i] *= gain;
            most = Math.Max(most, cutDb);
        }
        if (envelope < 1e-20)
        {
            envelope = 0.0; // out of denormals in silence
        }
        _envelope = envelope;
        _cutDb = cutDb;
        _meter.Raise((float)most);
    }

    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 1) return 0;
        into[0] = _meter.Take();
        return 1;
    }
}
