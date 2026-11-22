// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/dynamics.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>Gain that opens and closes with attack, hold and release, in dB.
/// Audio thread only. A mutable struct: keep it in a field and call it there,
/// never on a copy.</summary>
internal struct GainRider
{
    private double _rate;
    private double _gainDb;
    private double _held; // samples of hold left
    private double _attack; // one-pole coefficients
    private double _release;
    private double _holdSamples;

    public GainRider()
    {
        _rate = 48000.0;
    }

    public readonly double GainDb => _gainDb;

    public void Prepare(double sampleRate)
    {
        _rate = sampleRate;
        _held = 0.0;
    }

    public void Reset(double gainDb) => _gainDb = gainDb;

    /// <summary>Times for the coming block, in ms (no allocation).</summary>
    public void SetTimes(double attackMs, double holdMs, double releaseMs)
    {
        _attack = StudioMath.Coefficient(attackMs, _rate);
        _release = StudioMath.Coefficient(releaseMs, _rate);
        _holdSamples = holdMs * 0.001 * _rate;
    }

    /// <summary>One sample: rides towards activeDb while <paramref name="active"/>
    /// (with the attack time), and for the hold time after, then back towards
    /// restDb (with the release time). Returns the linear gain.</summary>
    public double Step(bool active, double activeDb, double restDb)
    {
        if (active)
        {
            _held = _holdSamples;
        }
        else if (_held > 0.0)
        {
            _held -= 1.0;
            active = true;
        }
        double target = active ? activeDb : restDb;
        double k = active ? _attack : _release;
        _gainDb = target + ((_gainDb - target) * k);
        return Math.Pow(10.0, _gainDb / 20.0);
    }
}

/// <summary>A noise gate: below the threshold (with 4 dB of hysteresis) the
/// sound is turned down by "range" dB, so breaths, keyboard and room noise
/// between words don't get through. Parameters: "threshold" (dB), "range" (dB
/// of cut when closed), "attack", "hold", "release" (ms).</summary>
internal sealed class NoiseGateEffect : StudioEffect
{
    public const double HysteresisDb = 4.0;

    // Parameter positions, as in StudioCatalogue.
    private const int Threshold = 0;
    private const int Range = 1;
    private const int Attack = 2;
    private const int Hold = 3;
    private const int Release = 4;

    private HeldMeter _meter; // the cut now (dB, positive), the largest since the last read

    // Audio thread only.
    private GainRider _rider = new();
    private double _envelope;
    private double _envelopeFall; // per sample, ~10 ms
    private bool _open;

    public NoiseGateEffect() : base(StudioCatalogue.Gate) { }

    public override void Prepare(double sampleRate)
    {
        _rider.Prepare(sampleRate);
        _rider.Reset(-Params[Range]); // starts closed, so nothing pops through
        _envelope = 0.0;
        _envelopeFall = StudioMath.Coefficient(10.0, sampleRate);
        _open = false;
    }

    public override void Resume()
    {
        _rider.Reset(-Params[Range]); // closed again, as when new
        _envelope = 0.0;
        _open = false;
    }

    public override void Process(Span<float> block)
    {
        var p = Params;
        double threshold = p[Threshold];
        double range = p[Range];
        _rider.SetTimes(p[Attack], p[Hold], p[Release]);
        double envelope = _envelope;
        double envelopeFall = _envelopeFall;
        bool open = _open;
        double cut = 0.0;
        for (int i = 0; i < block.Length; i++)
        {
            envelope = Math.Max(Math.Abs((double)block[i]), envelope * envelopeFall);
            double level = 20.0 * Math.Log10(Math.Max(envelope, 1e-9));
            // Opens at the threshold, closes a little below it.
            open = open ? level > threshold - HysteresisDb : level > threshold;
            float gain = (float)_rider.Step(open, 0.0, -range);
            block[i] *= gain;
            cut = Math.Max(cut, -_rider.GainDb);
        }
        if (envelope < 1e-20)
        {
            envelope = 0.0; // out of denormals in silence
        }
        _envelope = envelope;
        _open = open;
        _meter.Raise((float)cut);
    }

    public override int ReadMeters(Span<float> into)
    {
        if (into.Length < 1) return 0;
        into[0] = _meter.Take();
        return 1;
    }
}
