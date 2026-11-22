// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (tests/test_effects.cpp, commit 2608d58).

using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

public class DynamicsTests
{
    private const double Rate = Signals.Rate;

    /// <summary>The steady-state amplitude of a sine at freq through effect
    /// (after 0.5 s), from the RMS: at high frequencies the sample peaks miss
    /// the true peak.</summary>
    private static double SinePeak(StudioEffect effect, double freq, double amplitude = 0.25)
    {
        var x = Signals.Sine(freq, amplitude, (int)Rate); // 1 s
        Signals.Run(effect, x, 1024);
        return Signals.Rms(x.AsSpan(x.Length / 2)) * Math.Sqrt(2.0);
    }

    private static double GainDb(double peak) => 20.0 * Math.Log10(peak / 0.25);

    private static float[] Constant(int samples, float value)
    {
        var x = new float[samples];
        Array.Fill(x, value);
        return x;
    }

    /// <summary>Runs a sine through while the parameters keep changing; every
    /// sample must stay finite.</summary>
    private static void AssertFiniteWhileChanging(StudioEffect effect,
        params Dictionary<string, double>[] settings)
    {
        effect.Prepare(Rate);
        var x = Signals.Sine(1000, 0.5, 48000);
        for (int start = 0, n = 0; start < x.Length; start += 256, n++)
        {
            effect.SetParams(settings[n % settings.Length]);
            effect.Process(x.AsSpan(start, Math.Min(256, x.Length - start)));
        }
        foreach (float v in x) Assert.True(float.IsFinite(v));
    }

    // --- Gate ---------------------------------------------------------------------

    [Fact]
    public void GateClosesOnQuiet()
    {
        var gate = new NoiseGateEffect(); // threshold -50 dB, range 40 dB
        gate.Prepare(Rate);
        // Quiet noise floor (about -66 dBFS): turned down by the range.
        double quiet = 20.0 * Math.Log10(SinePeak(gate, 1000.0, 0.0005) / 0.0005);
        Assert.True(quiet < -35.0, quiet.ToString());
        var meter = new float[1];
        Assert.Equal(1, gate.ReadMeters(meter));
        Assert.True(meter[0] > 35.0f);
        // Speech level: passes untouched.
        Assert.True(Math.Abs(GainDb(SinePeak(gate, 1000.0))) < 0.1);
    }

    [Fact]
    public void GateDoesNotAllocate()
    {
        var gate = new NoiseGateEffect();
        gate.Prepare(Rate);
        Assert.Equal(0, Signals.AllocatedBy(gate, Signals.Sine(1000, 0.5, 48000)));
    }

    [Fact]
    public void GateStaysFiniteWhileParametersChange()
    {
        AssertFiniteWhileChanging(new NoiseGateEffect(),
            Signals.Params(("threshold", -10.0), ("range", 80.0), ("attack", 0.1), ("hold", 0.0), ("release", 5.0)),
            Signals.Params(("threshold", -80.0), ("range", 0.0), ("attack", 50.0), ("hold", 1000.0), ("release", 2000.0)),
            Signals.Params(("threshold", 0.0), ("range", 40.0)));
    }

    // --- Compressor -----------------------------------------------------------------

    [Fact]
    public void CompressorKnob()
    {
        var off = new CompressorEffect();
        off.SetParams(Signals.Params(("amount", 0.0)));
        off.Prepare(Rate);
        Assert.True(Math.Abs(GainDb(SinePeak(off, 1000.0))) < 0.05); // 0 %: no change

        // Full: 20 dB apart going in comes out much closer together.
        static double Through(double amplitude)
        {
            var full = new CompressorEffect();
            full.SetParams(Signals.Params(("amount", 100.0)));
            full.Prepare(Rate);
            return 20.0 * Math.Log10(SinePeak(full, 1000.0, amplitude));
        }
        double loud = Through(0.25); // -12 dBFS
        double quiet = Through(0.025); // -32 dBFS
        Assert.True(loud - quiet < 10.0, $"{loud} {quiet}");
        Assert.True(loud < -12.0 + 1.0, loud.ToString()); // not louder than it came

        // A sine 6 dB over a 3:1 threshold loses most of its static 4 dB: the
        // level holds the peaks, it isn't fooled by each cycle's dips. (Makeup
        // set to 0 here: the catalogue's default is the knob's at 40 %, where
        // Patch Prime's was 0.)
        var steady = new CompressorEffect();
        steady.SetParams(Signals.Params(("advanced", 1.0), ("threshold", -18.0), ("ratio", 3.0),
            ("makeup", 0.0)));
        steady.Prepare(Rate);
        double steadyDb = GainDb(SinePeak(steady, 200.0));
        Assert.True(steadyDb < -3.0 && steadyDb > -4.5, steadyDb.ToString());

        var meters = new CompressorEffect();
        meters.SetParams(Signals.Params(("amount", 100.0)));
        meters.Prepare(Rate);
        SinePeak(meters, 1000.0);
        var meter = new float[2];
        Assert.Equal(2, meters.ReadMeters(meter));
        Assert.True(meter[0] > 10.0f);
        Assert.Equal(-35.0f, meter[1]); // the knob's threshold
        Assert.Equal(0, meters.ReadMeters(new float[1])); // too short: nothing

        // Advanced: its own controls, the knob ignored.
        var advanced = new CompressorEffect();
        advanced.SetParams(Signals.Params(("amount", 100.0), ("advanced", 1.0), ("threshold", 0.0),
            ("ratio", 1.0), ("makeup", 6.0)));
        advanced.Prepare(Rate);
        Assert.True(Math.Abs(GainDb(SinePeak(advanced, 1000.0)) - 6.0) < 0.05);
    }

    [Fact]
    public void CompressorFullControlsStartAtTheKnob()
    {
        // The catalogue's starting full controls are where the knob stands at
        // its starting 40 %.
        var compressor = new CompressorEffect();
        var info = compressor.Info;
        var defaults = info.Defaults();
        double amount = defaults[0];
        var knob = CompressorEffect.FromAmount(amount);
        Assert.Equal(knob.Threshold, defaults[2], 9);
        Assert.Equal(knob.Ratio, defaults[3], 9);
        Assert.Equal(knob.Attack, defaults[4], 9);
        Assert.Equal(knob.Release, defaults[5], 9);
        Assert.Equal(knob.Makeup, defaults[6], 9);
    }

    [Fact]
    public void CompressorFromAmountEnds()
    {
        var none = CompressorEffect.FromAmount(0.0);
        Assert.Equal(-10.0, none.Threshold);
        Assert.Equal(1.0, none.Ratio);
        Assert.Equal(0.0, none.Makeup);
        var full = CompressorEffect.FromAmount(100.0);
        Assert.Equal(-35.0, full.Threshold);
        Assert.Equal(6.0, full.Ratio);
        Assert.Equal(0.5 * 35.0 * (1.0 - (1.0 / 6.0)), full.Makeup, 9);
    }

    [Fact]
    public void CompressorDoesNotAllocate()
    {
        var compressor = new CompressorEffect();
        compressor.SetParams(Signals.Params(("amount", 100.0)));
        compressor.Prepare(Rate);
        Assert.Equal(0, Signals.AllocatedBy(compressor, Signals.Sine(1000, 0.5, 48000)));
    }

    [Fact]
    public void CompressorStaysFiniteWhileParametersChange()
    {
        AssertFiniteWhileChanging(new CompressorEffect(),
            Signals.Params(("amount", 100.0)),
            Signals.Params(("advanced", 1.0), ("threshold", -60.0), ("ratio", 20.0), ("attack", 0.1),
                ("release", 10.0), ("makeup", 24.0)),
            Signals.Params(("amount", 0.0)),
            Signals.Params(("advanced", 1.0), ("threshold", 0.0), ("ratio", 1.0), ("attack", 200.0),
                ("release", 3000.0), ("makeup", -12.0)));
    }

    // --- De-esser -------------------------------------------------------------------

    [Fact]
    public void DeEsserCutsOnlyTheHighs()
    {
        // A voice's body (500 Hz) at -12 dBFS: untouched.
        var low = new DeEsserEffect();
        low.Prepare(Rate);
        double body = GainDb(SinePeak(low, 500.0));
        Assert.True(Math.Abs(body) < 0.2, body.ToString());
        // A hot "s" (8 kHz) at -12 dBFS: turned down.
        var high = new DeEsserEffect();
        high.Prepare(Rate);
        double cut = GainDb(SinePeak(high, 8000.0));
        Assert.True(cut < -5.0 && cut > -11.0, cut.ToString());
        var meter = new float[1];
        Assert.Equal(1, high.ReadMeters(meter));
        Assert.True(meter[0] > 9.0f); // at the 10 dB range
        // The same "s" quietly: under the threshold, untouched.
        var quiet = new DeEsserEffect();
        quiet.Prepare(Rate);
        Assert.True(Math.Abs(20.0 * Math.Log10(SinePeak(quiet, 8000.0, 0.01) / 0.01)) < 0.2);
        // Listen: only what it watches, so the 500 Hz all but goes.
        var listen = new DeEsserEffect();
        listen.SetParams(Signals.Params(("listen", 1.0)));
        listen.Prepare(Rate);
        Assert.True(GainDb(SinePeak(listen, 500.0)) < -30.0);
    }

    [Fact]
    public void DeEsserDoesNotAllocate()
    {
        var deEsser = new DeEsserEffect();
        deEsser.Prepare(Rate);
        Assert.Equal(0, Signals.AllocatedBy(deEsser, Signals.Sine(8000, 0.5, 48000)));
    }

    [Fact]
    public void DeEsserStaysFiniteWhileParametersChange()
    {
        AssertFiniteWhileChanging(new DeEsserEffect(),
            Signals.Params(("frequency", 2000.0), ("threshold", -60.0), ("range", 24.0)),
            Signals.Params(("frequency", 12000.0), ("threshold", 0.0), ("range", 0.0), ("listen", 1.0)),
            Signals.Params(("frequency", 4500.0), ("threshold", -40.0), ("range", 12.0)));
    }

    // --- Expander -------------------------------------------------------------------

    [Fact]
    public void ExpanderTurnsDownBelowThreshold()
    {
        var speech = new ExpanderEffect(); // threshold -45 dB, 1:2, range 20 dB
        speech.Prepare(Rate);
        Assert.True(Math.Abs(GainDb(SinePeak(speech, 1000.0))) < 0.05);
        // 15 dB under the threshold: down another 15 dB.
        var room = new ExpanderEffect();
        room.Prepare(Rate);
        double amplitude = Math.Pow(10.0, -60.0 / 20.0);
        double cut = 20.0 * Math.Log10(SinePeak(room, 1000.0, amplitude) / amplitude);
        Assert.True(Math.Abs(cut + 15.0) < 1.5, cut.ToString());
        // Far under: no more than the range.
        var silence = new ExpanderEffect();
        silence.Prepare(Rate);
        const double tiny = 1e-5;
        Assert.True(20.0 * Math.Log10(SinePeak(silence, 1000.0, tiny) / tiny) > -20.5);
        var meter = new float[1];
        Assert.Equal(1, silence.ReadMeters(meter));
        Assert.True(meter[0] > 19.0f);
    }

    [Fact]
    public void ExpanderDoesNotAllocate()
    {
        var expander = new ExpanderEffect();
        expander.Prepare(Rate);
        // Under the threshold, so it is cutting.
        Assert.Equal(0, Signals.AllocatedBy(expander, Signals.Sine(1000, 0.001, 48000)));
    }

    [Fact]
    public void ExpanderStaysFiniteWhileParametersChange()
    {
        AssertFiniteWhileChanging(new ExpanderEffect(),
            Signals.Params(("threshold", 0.0), ("ratio", 8.0), ("range", 60.0), ("attack", 0.1), ("release", 10.0)),
            Signals.Params(("threshold", -80.0), ("ratio", 1.0), ("range", 0.0), ("attack", 50.0), ("release", 2000.0)),
            Signals.Params(("threshold", -20.0), ("ratio", 4.0), ("range", 30.0)));
    }

    // --- Both -----------------------------------------------------------------------

    [Fact]
    public void GateAndExpanderStartClosedAfterResume()
    {
        var gate = new NoiseGateEffect();
        gate.Prepare(Rate);
        var expander = new ExpanderEffect();
        expander.Prepare(Rate);
        // Loud enough to open both.
        var loud = Constant(4800, 0.5f);
        gate.Process(loud);
        var loud2 = Constant(4800, 0.5f);
        expander.Process(loud2);
        Assert.True(loud[4799] > 0.4f);
        Assert.True(loud2[4799] > 0.4f);
        // Resumed: a quiet sound right after is held down, not let through.
        gate.Resume();
        expander.Resume();
        var quiet = Constant(48, 0.001f);
        gate.Process(quiet);
        var quiet2 = Constant(48, 0.001f);
        expander.Process(quiet2);
        Assert.True(Math.Abs(quiet[0]) < 0.0005f);
        Assert.True(Math.Abs(quiet2[0]) < 0.0005f);
    }
}
