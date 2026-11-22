// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (engine/src/loudness.cpp, commit 2608d58).

namespace SimpleHPSDR.Dsp.Studio;

/// <summary>True peak (ITU-R BS.1770-4 Annex 2) of one channel: each sample
/// interpolated four times over with the standard 48-tap filter; Process
/// gives the largest of the four (absolute, linear). The interpolated values
/// come about <see cref="Delay"/> samples late.</summary>
internal sealed class TruePeakDetector
{
    public const int Taps = 12; // per phase
    public const int Delay = 6;
    private const int Phases = 4;

    // BS.1770-4 Annex 2: the four phases of the 48-tap interpolator, one row each.
    private static readonly float[] PhaseTaps =
    [
        0.0017089843750f, 0.0109863281250f, -0.0196533203125f, 0.0332031250000f, -0.0594482421875f,
        0.1373291015625f, 0.9721679687500f, -0.1022949218750f, 0.0476074218750f, -0.0266113281250f,
        0.0148925781250f, -0.0083007812500f,

        -0.0291748046875f, 0.0292968750000f, -0.0517578125000f, 0.0891113281250f, -0.1665039062500f,
        0.4650878906250f, 0.7797851562500f, -0.2003173828125f, 0.1015625000000f, -0.0582275390625f,
        0.0330810546875f, -0.0189208984375f,

        -0.0189208984375f, 0.0330810546875f, -0.0582275390625f, 0.1015625000000f, -0.2003173828125f,
        0.7797851562500f, 0.4650878906250f, -0.1665039062500f, 0.0891113281250f, -0.0517578125000f,
        0.0292968750000f, -0.0291748046875f,

        -0.0083007812500f, 0.0148925781250f, -0.0266113281250f, 0.0476074218750f, -0.1022949218750f,
        0.9721679687500f, 0.1373291015625f, -0.0594482421875f, 0.0332031250000f, -0.0196533203125f,
        0.0109863281250f, 0.0017089843750f,
    ];

    private readonly float[] _history = new float[Taps]; // newest first

    public void Reset() => Array.Clear(_history);

    public float Process(float sample)
    {
        float[] history = _history;
        for (int i = history.Length - 1; i > 0; i--)
            history[i] = history[i - 1];
        history[0] = sample;
        float peak = 0f;
        for (int phase = 0; phase < Phases; phase++)
        {
            int row = phase * Taps;
            float y = 0f;
            for (int j = 0; j < Taps; j++)
                y += PhaseTaps[row + j] * history[j];
            peak = Math.Max(peak, Math.Abs(y));
        }
        return peak;
    }
}
