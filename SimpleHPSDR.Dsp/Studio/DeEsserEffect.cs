// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/voice.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A split-band de-esser. The sound is split at "frequency" (Hz) into
/// a low part and what's left over (the two add back up to the input
/// exactly); while the level above the frequency (through a highpass) is over
/// "threshold" (dB), only the upper part is turned down, at 4:1 and by up to
/// "range" dB, so the "s" softens and the rest of the voice doesn't move.
/// "listen" plays only what it watches, to find the frequency.</summary>
internal sealed class DeEsserEffect : StudioEffect
{
    public const double Ratio = 4.0;

    // Parameter positions, as in StudioCatalogue.
    private const int Frequency = 0;
    private const int Threshold = 1;
    private const int Range = 2;
    private const int Listen = 3;

    private HeldMeter _meter; // the cut (dB, positive), the largest since the last read

    // Audio thread only.
    private double _rate = 48000.0;
    private double _designed; // the frequency the filters were made for
    private BiquadCoefficients _filter; // the split's lowpass
    private BiquadCoefficients _detect; // the watched highpass
    private BiquadState _z;
    private BiquadState _zDetect;
    private double _envelope;
    private double _envelopeFall; // ~30 ms
    private double _cutDb;
    private double _attack; // ~1 ms
    private double _release; // ~60 ms

    public DeEsserEffect() : base(StudioCatalogue.DeEsser) { }

    public override void Prepare(double sampleRate)
    {
        _rate = sampleRate;
        _designed = 0.0;
        _envelopeFall = StudioMath.Coefficient(30.0, sampleRate);
        _attack = StudioMath.Coefficient(1.0, sampleRate);
        _release = StudioMath.Coefficient(60.0, sampleRate);
        Resume();
    }

    public override void Resume()
    {
        _z.Clear();
        _zDetect.Clear();
        _envelope = 0.0;
        _cutDb = 0.0;
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        double freq = p[Frequency];
        if (freq != _designed)
        {
            _filter = Biquad.Design(BiquadType.HighCut, freq, 0.0, 0.707, _rate);
            _detect = Biquad.Design(BiquadType.LowCut, freq, 0.0, 0.707, _rate);
            _designed = freq;
        }
        double threshold = p[Threshold];
        double range = p[Range];
        bool listen = p[Listen] >= 0.5;
        BiquadCoefficients filter = _filter;
        BiquadCoefficients detect = _detect;
        BiquadState z = _z;
        BiquadState zDetect = _zDetect;
        double envelope = _envelope;
        double envelopeFall = _envelopeFall;
        double cutDb = _cutDb;
        double attack = _attack;
        double release = _release;
        double most = 0.0;
        for (int i = 0; i < block.Length; i++)
        {
            double x = block[i];
            // What's turned down: the input less its low part, so the two
            // always add back up to the input exactly.
            double high = x - z.Step(in filter, x);
            // What's watched (and heard with Listen): a true highpass, so a
            // loud low voice doesn't set it off.
            double watched = zDetect.Step(in detect, x);
            envelope = Math.Max(Math.Abs(watched), envelope * envelopeFall);
            double over = StudioMath.ToDb(envelope) - threshold;
            double wanted = Math.Clamp(over * (1.0 - (1.0 / Ratio)), 0.0, range);
            double k = wanted > cutDb ? attack : release;
            cutDb = wanted + ((cutDb - wanted) * k);
            // The high part is taken out by as much as it's turned down.
            double takeOut = 1.0 - Math.Pow(10.0, -cutDb / 20.0);
            block[i] = (float)(listen ? watched : x - (takeOut * high));
            most = Math.Max(most, cutDb);
        }
        if (envelope < 1e-20)
        {
            envelope = 0.0; // out of denormals in silence
        }
        z.Flush(1e-30);
        zDetect.Flush(1e-30);
        _z = z;
        _zDetect = zDetect;
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
