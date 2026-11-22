// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// SimpleStudio effects: ported from Patch Prime (kingsqueak/patch-prime,
// engine/src/effects.h, commit 2608d58), for one channel.
//
// Threads: Prepare and SetParams run on a control thread; Process and Resume
// run on the TX audio thread and must not allocate, lock or throw. Parameters
// cross over as one immutable array (a volatile reference swap); meters come
// back through single-word values.

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

internal abstract class StudioEffect
{
    private volatile double[] _params;
    private volatile bool _enabled = true;

    protected StudioEffect(string type)
    {
        Info = StudioCatalogue.Find(type)
            ?? throw new ArgumentException($"unknown studio effect '{type}'", nameof(type));
        _params = Info.Defaults();
    }

    /// <summary>The effect's entry in the catalogue: its parameters, in the
    /// order <see cref="Params"/> holds them.</summary>
    public StudioEffectInfo Info { get; }
    public string Type => Info.Type;

    /// <summary>On or bypassed. A change crossfades (see StudioChain).</summary>
    public bool Enabled { get => _enabled; set => _enabled = value; }

    /// <summary>The current values, in catalogue order. Audio thread: read it
    /// once per block into a local; the array is never changed after it is set.</summary>
    protected double[] Params => _params;

    /// <summary>Control thread, while Process may be running: new values by
    /// name (made valid; missing ones take their default).</summary>
    public void SetParams(IReadOnlyDictionary<string, double>? values) => _params = Info.Values(values);

    /// <summary>Control thread, before the effect first runs: may allocate.</summary>
    public abstract void Prepare(double sampleRate);

    /// <summary>Audio thread: one block, in place. Any length.</summary>
    public abstract void Process(Span<float> block);

    /// <summary>Audio thread: the effect runs again after being skipped;
    /// forget what it held from before (buffered audio, filter memory).</summary>
    public virtual void Resume() { }

    /// <summary>True for an effect that takes care of its own bypass, to let
    /// what it holds ring out (a reverb's tail): the chain keeps calling
    /// Process, which reads <see cref="Enabled"/> itself, passes the dry signal
    /// when off and does not crossfade. Resume is then only called for a chain
    /// that was not running at all.</summary>
    public virtual bool RingsOut => false;

    /// <summary>Samples late the output comes, once prepared.</summary>
    public virtual int Latency => 0;

    /// <summary>Control thread: the effect's own meters since the last call
    /// (see StudioEffectMeterDto.Values) into <paramref name="into"/>; returns
    /// how many it wrote.</summary>
    public virtual int ReadMeters(Span<float> into) => 0;

    public const int MaxMeters = 8;
}

/// <summary>A meter that holds the largest value since it was last taken.
/// Raise on the audio thread, Take on another; neither can lose the other's
/// write.</summary>
internal struct HeldMeter
{
    private float _value;

    public void Raise(float value)
    {
        float current = Volatile.Read(ref _value);
        while (value > current)
        {
            float seen = Interlocked.CompareExchange(ref _value, value, current);
            if (seen == current) break;
            current = seen;
        }
    }

    public float Take() => Interlocked.Exchange(ref _value, 0f);
}

internal static class StudioMath
{
    public const double KneeDb = 6.0;

    /// <summary>One-pole coefficient for a time constant in ms.</summary>
    public static double Coefficient(double ms, double sampleRate) =>
        Math.Exp(-1.0 / (Math.Max(ms, 0.01) * 0.001 * sampleRate));

    public static double ToDb(double level) => 20.0 * Math.Log10(Math.Max(level, 1e-9));

    public static double FromDb(double db) => Math.Pow(10.0, db / 20.0);

    /// <summary>A compressor's output level for an input level (all dB), with
    /// threshold, ratio and the 6 dB soft knee.</summary>
    public static double CompressorCurve(double inputDb, double thresholdDb, double ratio)
    {
        double over = inputDb - thresholdDb;
        if (2.0 * over < -KneeDb) return inputDb;
        if (2.0 * Math.Abs(over) <= KneeDb)
        {
            double into = over + (KneeDb / 2.0);
            return inputDb + ((1.0 / ratio - 1.0) * into * into / (2.0 * KneeDb));
        }
        return thresholdDb + (over / ratio);
    }
}
