// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/effects.cpp, EffectChain, commit
// 2608d58), for one channel, with one change: a chain is built from the one
// before it and keeps the effects whose ids it still has, so rearranging the
// chain does not reset their state.

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>Effects in order. Each one's bypass crossfades between its dry
/// input and its output over <see cref="CrossfadeSeconds"/>, so switching
/// never clicks; a fully bypassed effect isn't processed at all (except one
/// that rings out, see <see cref="StudioEffect.RingsOut"/>, which does this
/// itself). The level is measured before the first effect and after each one
/// while someone is looking.
///
/// A chain's list of effects never changes: a different list is a new chain
/// (see <see cref="Build"/>), swapped in by reference. Threads: Build and
/// ReadMeters on a control thread, Process and Resume on the audio thread.</summary>
internal sealed class StudioChain
{
    public const double CrossfadeSeconds = 0.010;
    public const double RmsSeconds = 0.3;
    public const double FloorDb = -120.0;
    private const int MaxBlock = 4096;

    /// <summary>An effect and its place in the bypass crossfade. Kept from
    /// chain to chain with the effect.</summary>
    private sealed class Slot(string id, StudioEffect effect)
    {
        public readonly string Id = id;
        public readonly StudioEffect Effect = effect;
        public float Mix = 1f; // 0 = bypassed, 1 = on; audio thread only
    }

    /// <summary>Where the level is measured.</summary>
    private sealed class Point
    {
        public HeldMeter Peak;
        public float Rms;          // written by the audio thread
        public double MeanSquare;  // audio thread only
    }

    private readonly Slot[] _slots;
    private readonly Point[] _points; // one more than effects
    private readonly float[] _dry = new float[MaxBlock];
    private readonly float _step;     // crossfade change per sample

    private StudioChain(Slot[] slots, double sampleRate)
    {
        _slots = slots;
        SampleRate = sampleRate;
        _points = new Point[slots.Length + 1];
        for (int i = 0; i < _points.Length; i++) _points[i] = new Point();
        _step = (float)(1.0 / Math.Max(1.0, CrossfadeSeconds * sampleRate));
    }

    public double SampleRate { get; }
    public int Count => _slots.Length;

    /// <summary>The latency of the effects that are on, together (samples).</summary>
    public int Latency
    {
        get
        {
            int total = 0;
            foreach (var slot in _slots)
                if (slot.Effect.Enabled) total += slot.Effect.Latency;
            return total;
        }
    }

    public static StudioEffect? Create(string type) => type switch
    {
        StudioCatalogue.Eq => new ParametricEqEffect(),
        StudioCatalogue.Compressor => new CompressorEffect(),
        StudioCatalogue.DeEsser => new DeEsserEffect(),
        StudioCatalogue.Multiband => new MultibandEffect(),
        StudioCatalogue.Gate => new NoiseGateEffect(),
        StudioCatalogue.Expander => new ExpanderEffect(),
        StudioCatalogue.Leveler => new LevelerEffect(),
        StudioCatalogue.Limiter => new LimiterEffect(),
        StudioCatalogue.Reverb => new ReverbEffect(),
        StudioCatalogue.Gain => new GainEffect(),
        _ => null,
    };

    /// <summary>Control thread. The chain for a (normalized) config: an effect
    /// the previous chain has under the same id and type is kept, running
    /// state and all, and only given its new settings; the others are made
    /// and prepared here. Returns <paramref name="previous"/> itself when it
    /// already has exactly these effects in this order.</summary>
    public static StudioChain Build(SimpleStudioConfig config, double sampleRate, StudioChain? previous)
    {
        if (previous is not null && previous.SampleRate != sampleRate) previous = null;
        var wanted = config.Effects ?? [];
        var slots = new List<Slot>(wanted.Count);
        foreach (var e in wanted)
        {
            Slot? slot = null;
            if (previous is not null)
                foreach (var old in previous._slots)
                    if (old.Id == e.Id && old.Effect.Type == e.Type) { slot = old; break; }
            if (slot is not null)
            {
                slot.Effect.SetParams(e.Params);
                slot.Effect.Enabled = e.Enabled;
            }
            else
            {
                if (Create(e.Type) is not { } effect) continue;
                effect.SetParams(e.Params);
                effect.Enabled = e.Enabled;
                effect.Prepare(sampleRate);
                slot = new Slot(e.Id, effect) { Mix = e.Enabled ? 1f : 0f };
            }
            slots.Add(slot);
        }
        if (previous is not null && previous._slots.Length == slots.Count)
        {
            bool same = true;
            for (int i = 0; i < slots.Count; i++) same &= ReferenceEquals(previous._slots[i], slots[i]);
            if (same) return previous;
        }
        return new StudioChain([.. slots], sampleRate);
    }

    /// <summary>Audio thread: one block, in place. measure: also measure the
    /// levels. Non-finite samples come in as silence: one would stick in the
    /// effects' filters and envelopes for good.</summary>
    public void Process(Span<float> block, bool measure)
    {
        for (int i = 0; i < block.Length; i++)
            if (!float.IsFinite(block[i])) block[i] = 0f;
        for (int start = 0; start < block.Length; start += MaxBlock)
            ProcessPart(block.Slice(start, Math.Min(MaxBlock, block.Length - start)), measure);
    }

    private void ProcessPart(Span<float> block, bool measure)
    {
        double rmsStep = measure
            ? 1.0 - Math.Exp(-block.Length / (RmsSeconds * Math.Max(SampleRate, 1.0)))
            : 0.0;
        if (measure) Measure(_points[0], block, rmsStep);
        for (int e = 0; e < _slots.Length; e++)
        {
            var slot = _slots[e];
            var effect = slot.Effect;
            if (effect.RingsOut)
            {
                effect.Process(block);
            }
            else
            {
                float mix = slot.Mix;
                float target = effect.Enabled ? 1f : 0f;
                if (mix == 0f && target == 0f)
                {
                    // Bypassed: not processed at all, and its output is its input.
                }
                else
                {
                    if (mix == 0f) effect.Resume(); // coming back from bypass
                    if (mix == 1f && target == 1f)
                    {
                        effect.Process(block);
                    }
                    else
                    {
                        // Switching: process, then crossfade from the dry signal.
                        var dry = _dry.AsSpan(0, block.Length);
                        block.CopyTo(dry);
                        effect.Process(block);
                        for (int i = 0; i < block.Length; i++)
                        {
                            mix = target > mix ? Math.Min(target, mix + _step) : Math.Max(target, mix - _step);
                            block[i] = dry[i] + ((block[i] - dry[i]) * mix);
                        }
                        slot.Mix = mix;
                    }
                }
            }
            if (measure) Measure(_points[e + 1], block, rmsStep);
        }
    }

    /// <summary>Audio thread: every effect forgets what it holds (delay lines,
    /// envelopes), as when it comes back from bypass. For a chain that was not
    /// running, so nothing from before comes out.</summary>
    public void Resume()
    {
        foreach (var slot in _slots)
        {
            slot.Effect.Resume();
            if (!slot.Effect.RingsOut) slot.Mix = slot.Effect.Enabled ? 1f : 0f;
        }
    }

    private static void Measure(Point point, ReadOnlySpan<float> block, double rmsStep)
    {
        if (block.IsEmpty) return;
        float peak = 0f;
        double sum = 0.0;
        foreach (float sample in block)
        {
            float level = Math.Abs(sample);
            if (level > peak) peak = level;
            sum += (double)level * level;
        }
        double meanSquare = point.MeanSquare;
        meanSquare += ((sum / block.Length) - meanSquare) * rmsStep;
        if (meanSquare < 1e-20) meanSquare = 0.0; // out of denormals in silence
        point.MeanSquare = meanSquare;
        point.Peak.Raise(peak);
        Volatile.Write(ref point.Rms, (float)Math.Sqrt(meanSquare));
    }

    /// <summary>Control thread: each effect's levels and own meters, in chain
    /// order. Peaks and reductions are since the last call (an effect's output
    /// is the next one's input, so they're all taken together).</summary>
    public StudioEffectMeterDto[] ReadMeters()
    {
        static double Db(float level) => level > 1e-6f ? 20.0 * Math.Log10(level) : FloorDb;
        var peaks = new double[_points.Length];
        var rms = new double[_points.Length];
        for (int p = 0; p < _points.Length; p++)
        {
            peaks[p] = Db(_points[p].Peak.Take());
            rms[p] = Db(Volatile.Read(ref _points[p].Rms));
        }
        var result = new StudioEffectMeterDto[_slots.Length];
        Span<float> own = stackalloc float[StudioEffect.MaxMeters];
        for (int e = 0; e < _slots.Length; e++)
        {
            int n = _slots[e].Effect.ReadMeters(own);
            var values = new double[n];
            for (int i = 0; i < n; i++) values[i] = own[i];
            result[e] = new StudioEffectMeterDto(_slots[e].Id, peaks[e], rms[e], peaks[e + 1], rms[e + 1], values);
        }
        return result;
    }
}
