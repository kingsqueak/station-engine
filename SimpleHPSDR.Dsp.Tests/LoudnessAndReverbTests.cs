// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

/// <summary>The leveler, true-peak detector, limiter and reverb; ported from
/// Patch Prime's tests/test_effects.cpp and tests/test_loudness.cpp, for one
/// channel.</summary>
public class LoudnessAndReverbTests
{
    // --- Leveler -------------------------------------------------------------

    [Fact]
    public void LoudnessReference()
    {
        // BS.1770: a 0 dBFS 997 Hz sine in one channel reads -3.01 LUFS.
        var meter = new LevelerEffect();
        meter.SetParams(Signals.Params(("maxBoost", 0.0), ("maxCut", 0.0)));
        meter.Prepare(Signals.Rate);
        var tone = Signals.Sine(997.0, 1.0, 48000 * 5);
        meter.Process(tone);
        var values = new float[2];
        Assert.Equal(2, meter.ReadMeters(values));
        Assert.True(Math.Abs(values[1] + 3.01f) < 0.1f, values[1].ToString());
        Assert.Equal(0f, values[0]);
    }

    [Fact]
    public void KWeightingReadsTheSineAtMinus3()
    {
        // The same, straight through the two filters: 1 kHz full scale.
        var (shelf, highPass) = KWeighting.ForRate(Signals.Rate);
        var a = new BiquadState();
        var b = new BiquadState();
        var tone = Signals.Sine(1000.0, 1.0, 48000 * 2);
        double sum = 0.0;
        for (int i = 0; i < tone.Length; i++)
        {
            double y = b.Step(in highPass, a.Step(in shelf, tone[i]));
            if (i >= 48000) sum += y * y;
        }
        Assert.Equal(-3.01, KWeighting.LufsFromMeanSquare(sum / 48000), 1);
    }

    [Fact]
    public void LevelerRidesToTarget()
    {
        // A quiet tone at -30 LUFS, target -18: boosted 12 dB. One channel at
        // amplitude a reads 20 log10(a) - 3.01 LUFS.
        double amplitude = Amplitude(-30.0);
        var leveler = Leveler(("speed", 1.0), ("maxBoost", 24.0));
        var values = RunLeveler(leveler, amplitude, 12);
        Assert.True(Math.Abs(values[1] + 30f) < 0.3f, values[1].ToString());
        Assert.True(Math.Abs(values[0] - 12f) < 0.5f, values[0].ToString());
        // Silence: the gain holds where it was (no pumping up the noise).
        values = RunLeveler(leveler, 0.0, 6);
        Assert.True(Math.Abs(values[0] - 12f) < 1f, values[0].ToString());

        // Coming back from silence: measured afresh, not against the silence
        // (a tone already at the target needs no boost when it returns).
        var steady = Leveler(("speed", 0.5), ("maxBoost", 24.0));
        double atTarget = Amplitude(-18.0);
        RunLeveler(steady, atTarget, 5);
        RunLeveler(steady, 0.0, 5);
        values = RunLeveler(steady, atTarget, 1);
        Assert.True(Math.Abs(values[0]) < 1f, values[0].ToString());

        // Max boost limits it.
        var capped = Leveler(("speed", 1.0), ("maxBoost", 6.0));
        values = RunLeveler(capped, amplitude, 12);
        Assert.True(Math.Abs(values[0] - 6f) < 0.3f, values[0].ToString());

        // A very quiet source (-48 LUFS, above the -50 freeze) comes up 30 dB to -18.
        double faint = Amplitude(-48.0);
        var quiet = Leveler(("speed", 1.0), ("maxBoost", 35.0));
        values = RunLeveler(quiet, faint, 14);
        Assert.True(Math.Abs(values[0] - 30f) < 0.5f, values[0].ToString());

        // And the most it will give is 35 dB, whatever it's asked for: target -6
        // from -48 would need 42 (the catalogue holds maxBoost at 35).
        var most = Leveler(("speed", 1.0), ("target", -6.0), ("maxBoost", 50.0));
        values = RunLeveler(most, faint, 14);
        Assert.True(Math.Abs(values[0] - 35f) < 0.5f, values[0].ToString());
    }

    [Fact]
    public void LevelerDoesNotAllocate()
    {
        var leveler = Leveler(("maxBoost", 24.0));
        Assert.Equal(0, Signals.AllocatedBy(leveler, Signals.Sine(997, 0.01, 48000)));
    }

    [Fact]
    public void LevelerParamChangesStayFinite()
    {
        var leveler = Leveler();
        AssertFiniteWhileChanging(leveler, i => Signals.Params(
            ("target", i % 2 == 0 ? -36.0 : -6.0), ("maxBoost", 35.0), ("maxCut", 24.0),
            ("speed", i % 3 == 0 ? 0.5 : 30.0), ("freeze", i % 2 == 0 ? -70.0 : -20.0)));
    }

    // --- True peak -----------------------------------------------------------

    [Fact]
    public void TruePeakFindsPeaksBetweenSamples()
    {
        // A sine at a quarter of the rate, 45 degrees off: every sample is at
        // 0.707, but the wave peaks at 1 between them.
        var detector = new TruePeakDetector();
        float samplePeak = 0f;
        float truePeak = 0f;
        for (int i = 0; i < 400; i++)
        {
            float x = (float)Math.Sin((Math.PI / 2.0 * i) + (Math.PI / 4.0));
            samplePeak = Math.Max(samplePeak, Math.Abs(x));
            float p = detector.Process(x);
            if (i > 20) truePeak = Math.Max(truePeak, p);
        }
        Assert.True(samplePeak < 0.71f, samplePeak.ToString());
        Assert.True(truePeak > 0.95f && truePeak < 1.05f, truePeak.ToString());
    }

    // --- Limiter -------------------------------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    public void LimiterLatencyIsWhereTheSoundComesOut(double truePeak)
    {
        // An impulse under the ceiling comes out exactly the latency later, in both modes.
        var limiter = new LimiterEffect();
        limiter.SetParams(Signals.Params(("truePeak", truePeak)));
        limiter.Prepare(Signals.Rate);
        Assert.Equal(LookaheadSamples(), limiter.Latency);
        var samples = new float[1024];
        samples[100] = 0.5f;
        limiter.Process(samples);
        int at = 0;
        for (int i = 1; i < samples.Length; i++)
            if (Math.Abs(samples[i]) > Math.Abs(samples[at])) at = i;
        Assert.Equal(100 + limiter.Latency, at);
    }

    [Fact]
    public void LimiterHoldsTheCeiling()
    {
        float ceiling = MathF.Pow(10f, -1f / 20f);
        // A full-scale tone: nothing over the ceiling, and close to it.
        var limiter = new LimiterEffect();
        limiter.Prepare(Signals.Rate);
        var tone = Signals.Sine(440.0, 1.0, 48000);
        limiter.Process(tone);
        float peak = (float)Signals.Peak(tone);
        Assert.True(peak <= ceiling);
        Assert.True(peak > ceiling * 0.95f);
        var meter = new float[1];
        Assert.Equal(1, limiter.ReadMeters(meter));
        Assert.True(Math.Abs(meter[0] - 1f) < 0.2f, meter[0].ToString());

        // A lone spike in quiet: caught before it arrives, not clipped flat.
        var spiky = new LimiterEffect();
        spiky.Prepare(Signals.Rate);
        var spike = new float[4800];
        Array.Fill(spike, 0.1f);
        spike[2000] = 1f;
        spiky.Process(spike);
        int delay = LookaheadSamples();
        Assert.True(spike[2000 + delay] <= ceiling);
        Assert.True(spike[2000 + delay] > ceiling * 0.99f);
        // Quieter audio passes unchanged, lookahead later.
        Assert.Equal(0.1f, spike[1000]);
        Assert.Equal(0f, spike[delay - 1]);
        Assert.Equal(0.1f, spike[delay]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LimiterHoldsTruePeaks(bool truePeak)
    {
        // In true-peak mode the quarter-rate sine (peaks between samples) is
        // held under the ceiling; in sample-peak mode it isn't.
        var limiter = new LimiterEffect();
        limiter.Prepare(Signals.Rate);
        limiter.SetParams(Signals.Params(("ceiling", -6.0), ("truePeak", truePeak ? 1.0 : 0.0)));
        var wave = new float[48000];
        for (int i = 0; i < wave.Length; i++)
            wave[i] = (float)(0.8 * Math.Sin((Math.PI / 2.0 * i) + (Math.PI / 4.0)));
        limiter.Process(wave);
        var detector = new TruePeakDetector();
        float peak = 0f;
        for (int i = 0; i < wave.Length; i++)
        {
            float p = detector.Process(wave[i]);
            if (i > 24000) peak = Math.Max(peak, p);
        }
        double db = 20.0 * Math.Log10(peak);
        if (truePeak)
            Assert.True(db < -5.9, db.ToString());
        else
            Assert.True(db > -3.5, db.ToString()); // samples at -6, the wave 3 dB over
    }

    [Fact]
    public void LimiterDoesNotAllocate()
    {
        var limiter = new LimiterEffect();
        limiter.SetParams(Signals.Params(("ceiling", -12.0), ("truePeak", 1.0)));
        limiter.Prepare(Signals.Rate);
        Assert.Equal(0, Signals.AllocatedBy(limiter, Signals.Sine(440, 0.9, 48000)));
    }

    [Fact]
    public void LimiterParamChangesStayFinite()
    {
        var limiter = new LimiterEffect();
        AssertFiniteWhileChanging(limiter, i => Signals.Params(
            ("ceiling", i % 2 == 0 ? -24.0 : 0.0), ("release", i % 3 == 0 ? 5.0 : 2000.0),
            ("truePeak", i % 2)));
    }

    // --- Reverb --------------------------------------------------------------

    [Fact]
    public void ReverbDecaysAtItsTime()
    {
        // Mix 0: untouched.
        var dry = Reverb(("mix", 0.0));
        var same = new float[4800];
        Array.Fill(same, 0.3f);
        dry.Process(same);
        Assert.All(same, v => Assert.Equal(0.3f, v));

        // An impulse into a 2 s reverb, fully wet: the tail falls about
        // 30 dB in a second, and is gone after several.
        var reverb = Reverb(("mix", 100.0), ("decay", 2.0), ("predelay", 0.0));
        var tail = new float[48000 * 8];
        tail[0] = 1f;
        Signals.Run(reverb, tail, 480);
        double RmsDb(double from, double to)
        {
            int a = (int)(from * Signals.Rate);
            int b = (int)(to * Signals.Rate);
            double sum = 0.0;
            for (int i = a; i < b; i++) sum += (double)tail[i] * tail[i];
            return 10.0 * Math.Log10((sum / (b - a)) + 1e-30);
        }
        double drop = RmsDb(0.3, 0.4) - RmsDb(1.3, 1.4);
        Assert.True(Math.Abs(drop - 30.0) < 6.0, drop.ToString());
        Assert.True(RmsDb(7.0, 8.0) < RmsDb(0.3, 0.4) - 90.0);
        Assert.All(tail, v => Assert.True(float.IsFinite(v) && Math.Abs(v) < 2f));
    }

    [Fact]
    public void ReverbStaysBounded()
    {
        // The longest decay, the biggest room, loud noise for 10 s.
        var reverb = Reverb(("mix", 100.0), ("decay", 10.0), ("size", 100.0), ("damping", 0.0));
        var noise = Signals.Noise(0.5, 480 * 1000, 2);
        float peak = 0f;
        for (int block = 0; block < 1000; block++)
        {
            if (block == 500) // a size change mid-stream
                reverb.SetParams(Signals.Params(("mix", 100.0), ("decay", 10.0), ("size", 0.0), ("damping", 0.0)));
            var b = noise.AsSpan(block * 480, 480);
            reverb.Process(b);
            foreach (float v in b)
            {
                Assert.True(float.IsFinite(v));
                peak = Math.Max(peak, Math.Abs(v));
            }
        }
        Assert.True(peak < 8f, peak.ToString());
    }

    [Fact]
    public void ReverbRingsOutWhenBypassed()
    {
        // The effect takes care of its own bypass (RingsOut), so it is driven
        // directly here, as the chain would: Process keeps being called.
        double phase = 0.0;
        float last = 0f;
        (float Peak, float Jump) Run(ReverbEffect r, int blocks, float tone)
        {
            float peak = 0f, jump = 0f;
            var block = new float[480];
            for (int b = 0; b < blocks; b++)
            {
                for (int i = 0; i < block.Length; i++)
                {
                    phase += 2.0 * Math.PI * 440.0 / Signals.Rate;
                    block[i] = tone * (float)Math.Sin(phase);
                }
                r.Process(block);
                foreach (float v in block)
                {
                    peak = Math.Max(peak, Math.Abs(v));
                    jump = Math.Max(jump, Math.Abs(v - last));
                    last = v;
                }
            }
            return (peak, jump);
        }

        var reverb = Reverb(("mix", 100.0), ("decay", 0.5), ("predelay", 0.0));
        Assert.True(reverb.RingsOut);
        // A tone fills the room; bypassed in silence, the tail is still there
        // a quarter of a second later, and it doesn't step when switching.
        Run(reverb, 100, 0.3f);
        float ringing = Run(reverb, 10, 0.3f).Peak;
        float steady = Run(reverb, 10, 0.3f).Jump;
        reverb.Enabled = false;
        var switching = Run(reverb, 10, 0.3f);
        Assert.True(switching.Jump < (steady * 2f) + 0.05f, switching.Jump.ToString());
        Assert.True(switching.Peak > ringing * 0.5f);
        Run(reverb, 15, 0f);
        float tail = Run(reverb, 10, 0f).Peak;
        Assert.True(tail > 0.001f, tail.ToString());

        // Switched back on, the tail carries on rather than being wiped.
        reverb.Enabled = true;
        float kept = Run(reverb, 2, 0f).Peak;
        Assert.True(kept > tail * 0.3f, kept.ToString());

        // Once the tail has gone it sleeps: the output is the input, bit for
        // bit. And so it is when bypassed from the start.
        reverb.Enabled = false;
        Run(reverb, 500, 0f); // 5 s
        Assert.True(IsDry(reverb));

        var off = new ReverbEffect { Enabled = false };
        off.SetParams(Signals.Params(("mix", 50.0), ("decay", 0.5), ("predelay", 0.0)));
        off.Prepare(Signals.Rate);
        Assert.True(IsDry(off));
    }

    [Fact]
    public void ReverbResumeForgetsTheTail()
    {
        var reverb = Reverb(("mix", 100.0));
        var tone = Signals.Sine(440, 0.3, 9600);
        Signals.Run(reverb, tone, 480);
        reverb.Resume();
        var silence = new float[480];
        reverb.Process(silence);
        Assert.True(Signals.Peak(silence) < 1e-6);
    }

    [Fact]
    public void ReverbDoesNotAllocate()
    {
        var reverb = Reverb(("mix", 50.0));
        Assert.Equal(0, Signals.AllocatedBy(reverb, Signals.Noise(0.3, 48000)));
    }

    [Fact]
    public void ReverbParamChangesStayFinite()
    {
        var reverb = Reverb();
        AssertFiniteWhileChanging(reverb, i => Signals.Params(
            ("size", i % 2 == 0 ? 0.0 : 100.0), ("decay", i % 3 == 0 ? 0.2 : 10.0),
            ("predelay", i % 2 == 0 ? 0.0 : 200.0), ("damping", i % 4 * 33.0), ("mix", i % 2 == 0 ? 100.0 : 0.0)));
    }

    // --- Helpers -------------------------------------------------------------

    /// <summary>One channel's amplitude for a 997 Hz sine at that loudness.</summary>
    private static double Amplitude(double lufs) => Math.Pow(10.0, (lufs + 3.01) / 20.0);

    private static int LookaheadSamples() =>
        (int)Math.Round(LimiterEffect.LookaheadSeconds * Signals.Rate, MidpointRounding.AwayFromZero)
        + TruePeakDetector.Delay;

    private static LevelerEffect Leveler(params (string Name, double Value)[] values)
    {
        var leveler = new LevelerEffect();
        leveler.SetParams(Signals.Params(values));
        leveler.Prepare(Signals.Rate);
        return leveler;
    }

    private static ReverbEffect Reverb(params (string Name, double Value)[] values)
    {
        var reverb = new ReverbEffect();
        reverb.SetParams(Signals.Params(values));
        reverb.Prepare(Signals.Rate);
        return reverb;
    }

    private static float[] RunLeveler(LevelerEffect leveler, double amplitude, int seconds)
    {
        var tone = Signals.Sine(997.0, amplitude, 48000 * seconds);
        Signals.Run(leveler, tone, 512);
        var values = new float[2];
        leveler.ReadMeters(values);
        return values;
    }

    private static bool IsDry(ReverbEffect reverb)
    {
        var block = new float[480];
        Array.Fill(block, 0.25f);
        reverb.Process(block);
        return Array.TrueForAll(block, v => v == 0.25f);
    }

    /// <summary>Loud noise in blocks, new parameters before each one: every
    /// sample stays finite.</summary>
    private static void AssertFiniteWhileChanging(StudioEffect effect,
        Func<int, Dictionary<string, double>> values)
    {
        effect.Prepare(Signals.Rate);
        var noise = Signals.Noise(0.9, 256 * 200, 3);
        for (int i = 0; i < 200; i++)
        {
            effect.SetParams(values(i));
            var block = noise.AsSpan(i * 256, 256);
            effect.Process(block);
            foreach (float v in block) Assert.True(float.IsFinite(v));
        }
    }
}
