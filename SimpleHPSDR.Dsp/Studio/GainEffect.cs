// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/effects.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>A level trim: "gainDb" from -24 to +24 dB, smoothed so moving it
/// doesn't zipper.</summary>
internal sealed class GainEffect : StudioEffect
{
    private const int GainDb = 0; // parameter positions, as in StudioCatalogue

    // Audio thread only.
    private float _current = 1f;
    private float _smoothing;

    public GainEffect() : base(StudioCatalogue.Gain) { }

    public override void Prepare(double sampleRate)
    {
        // About 20 ms to settle on a new level.
        _smoothing = (float)(1.0 - Math.Exp(-1.0 / (0.005 * sampleRate)));
        _current = (float)StudioMath.FromDb(Params[GainDb]);
    }

    public override void Process(Span<float> block)
    {
        float target = (float)StudioMath.FromDb(Params[GainDb]);
        float current = _current;
        for (int i = 0; i < block.Length; i++)
        {
            current += (target - current) * _smoothing;
            block[i] *= current;
        }
        _current = current;
    }
}
