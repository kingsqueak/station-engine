// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// SimpleStudio: the operator's chain of mic effects on the TX mic block,
// ahead of DEXP, SimpleExpander and WDSP's TXA (so ahead of the mic gain too:
// the levels it meters are the raw mic's).
//
// Threads: Process and NoteSkipped run on the audio thread. Configure, SetRate
// and ReadMeters may be called from any other thread; they build a new chain
// off the audio thread and hand it over with one reference swap. No locks and
// no allocation on the audio thread. Switched off, Process is never called:
// the caller checks Wanted (two volatile reads) and moves on.
//
// Switching the whole chain on or off fades between the mic and the chain's
// output over the chain's crossfade time, like switching one effect: the
// chain keeps running for those few milliseconds after it is switched off.

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

public sealed class SimpleStudio
{
    private const long MeterArmMs = 2000;
    private const long MeterStaleMs = 500;

    private readonly object _build = new();   // control threads only
    private volatile SimpleStudioConfig _config = SimpleStudioConfig.Default;
    private volatile StudioChain? _chain;
    private int _rateHz;                      // under _build

    private const int FadeBlock = 4096;
    private readonly float[] _dry = new float[FadeBlock]; // audio thread only
    private bool _needsResume = true;         // audio thread only
    private float _mix;                       // audio thread only: 0 = the mic as it came, 1 = the chain
    private long _meterArmedUntilMs = long.MinValue;
    private long _lastBlockMs = long.MinValue / 2;

    public SimpleStudioConfig Config => _config;

    /// <summary>New settings (made valid). Effects already running keep their
    /// state, matched by id; see <see cref="StudioChain.Build"/>.</summary>
    public void Configure(SimpleStudioConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_build)
        {
            var valid = config.Normalized();
            _config = valid;
            if (_rateHz > 0) _chain = StudioChain.Build(valid, _rateHz, _chain);
        }
    }

    /// <summary>The TX mic block's sample rate. A change rebuilds the chain
    /// from new (filters and delay lines are made for a rate).</summary>
    public void SetRate(int rateHz)
    {
        lock (_build)
        {
            if (rateHz == _rateHz) return;
            _rateHz = rateHz;
            _chain = rateHz > 0 ? StudioChain.Build(_config, rateHz, null) : null;
        }
    }

    /// <summary>Audio thread: true when Process has work to do (switched on,
    /// or still fading out after being switched off).</summary>
    public bool Wanted => _chain is { Count: > 0 } && (_config.Enabled || _mix > 0f);

    /// <summary>Samples late the mic comes out with the chain as it stands.</summary>
    public int Latency => _config.Enabled && _chain is { Count: > 0 } chain ? chain.Latency : 0;

    /// <summary>Audio thread: a block went by without the chain (off, or a
    /// transmission that bypasses it). What the effects hold is stale then.</summary>
    public void NoteSkipped()
    {
        _needsResume = true;
        _mix = 0f;
    }

    /// <summary>Audio thread: one mic block through the chain, in place.</summary>
    public void Process(Span<float> mic, int rateHz)
    {
        var chain = _chain;
        // A chain for another rate (the rate just changed and SetRate has not
        // run yet): pass the mic through rather than run filters made for the
        // wrong rate.
        if (chain is null || chain.Count == 0 || (int)chain.SampleRate != rateHz)
        {
            NoteSkipped();
            return;
        }
        if (_needsResume)
        {
            chain.Resume();
            _needsResume = false;
        }
        long now = Environment.TickCount64;
        bool measure = now <= Volatile.Read(ref _meterArmedUntilMs);
        float mix = _mix;
        float target = _config.Enabled ? 1f : 0f;
        if ((mix == 1f && target == 1f) || mic.Length > FadeBlock)
        {
            chain.Process(mic, measure);
            _mix = target;
        }
        else
        {
            // Switching on or off: run the chain, then crossfade with the mic.
            var dry = _dry.AsSpan(0, mic.Length);
            mic.CopyTo(dry);
            chain.Process(mic, measure);
            float step = (float)(1.0 / Math.Max(1.0, StudioChain.CrossfadeSeconds * rateHz));
            for (int i = 0; i < mic.Length; i++)
            {
                mix = target > mix ? Math.Min(target, mix + step) : Math.Max(target, mix - step);
                // A sample the chain cleaned (not finite) stays cleaned.
                float d = float.IsFinite(dry[i]) ? dry[i] : 0f;
                mic[i] = d + ((mic[i] - d) * mix);
            }
            _mix = mix;
        }
        Volatile.Write(ref _lastBlockMs, now);
    }

    /// <summary>Latest readings. Calling it keeps the level measurement on for
    /// two seconds.</summary>
    public SimpleStudioMetersDto ReadMeters()
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _meterArmedUntilMs, now + MeterArmMs);
        var chain = _chain;
        if (chain is null || !_config.Enabled || now - Volatile.Read(ref _lastBlockMs) > MeterStaleMs)
            return SimpleStudioMetersDto.Inactive;
        return new SimpleStudioMetersDto(true, chain.ReadMeters());
    }
}
