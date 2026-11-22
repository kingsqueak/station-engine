// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

public class GainEffectTests
{
    [Fact]
    public void GainSettlesOnItsLevel()
    {
        var gain = new GainEffect();
        gain.SetParams(Signals.Params(("gainDb", -6.0)));
        gain.Prepare(Signals.Rate);
        var x = Signals.Sine(1000, 0.5, 9600);
        Signals.Run(gain, x);
        Assert.Equal(Signals.Db(0.5) - 6.0, Signals.Db(Signals.Peak(x.AsSpan(4800))), 1);
    }

    [Fact]
    public void GainDoesNotAllocate()
    {
        var gain = new GainEffect();
        gain.Prepare(Signals.Rate);
        Assert.Equal(0, Signals.AllocatedBy(gain, Signals.Sine(1000, 0.5, 48000)));
    }
}
