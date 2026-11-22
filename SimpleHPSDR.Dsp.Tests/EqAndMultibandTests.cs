// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// Ported from Patch Prime (tests/test_effects.cpp, commit 2608d58).

using SimpleHPSDR.Contracts;
using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

public class EqAndMultibandTests
{
    private const double Rate = Signals.Rate;

    // The steady-state amplitude of a sine at freq through effect (after 0.5 s),
    // from the RMS: at high frequencies the sample peaks miss the true peak.
    private static double SinePeak(StudioEffect effect, double freq, double sampleRate = Rate,
        double amplitude = 0.25)
    {
        int frames = (int)sampleRate; // 1 s
        var x = Signals.Sine(freq, amplitude, frames, sampleRate);
        Signals.Run(effect, x, 1024);
        return Signals.Rms(x.AsSpan(frames / 2)) * Math.Sqrt(2.0);
    }

    private static double GainDb(double peak) => 20.0 * Math.Log10(peak / 0.25);

    // The drawn curve: the sum of each band's response, for the bands that are on.
    private static double ResponseDb(IReadOnlyDictionary<string, double> values, double freq, double rate)
    {
        var p = StudioCatalogue.Find(StudioCatalogue.Eq)!.Values(values);
        double db = 0.0;
        for (int b = 0; b < StudioCatalogue.EqBands; b++)
        {
            int at = b * 5;
            if (p[at] < 0.5) continue;
            var c = Biquad.Design((BiquadType)(int)p[at + 1], p[at + 2], p[at + 3], p[at + 4], rate);
            db += Biquad.ResponseDb(in c, freq, rate);
        }
        return db;
    }

    private static Dictionary<string, double> AllBands(int count, string field, double value)
    {
        var d = new Dictionary<string, double>();
        for (int b = 1; b <= count; b++) d[$"b{b}{field}"] = value;
        return d;
    }

    [Fact]
    public void EqFlatIsTransparent()
    {
        var eq = new ParametricEqEffect();
        eq.Prepare(Rate);
        foreach (double freq in new[] { 40.0, 1000.0, 15000.0 })
            Assert.True(Math.Abs(GainDb(SinePeak(eq, freq))) < 0.05, $"{freq}");
    }

    [Fact]
    public void EqBellAndCut()
    {
        var values = Signals.Params(("b3Gain", 6.0), ("b1Type", (double)BiquadType.LowCut), ("b1Freq", 200.0));
        var eq = new ParametricEqEffect();
        eq.SetParams(values);
        eq.Prepare(Rate);
        Assert.True(Math.Abs(GainDb(SinePeak(eq, 1000.0)) - 6.0) < 0.2);
        double at8k = GainDb(SinePeak(eq, 8000.0));
        Assert.True(Math.Abs(at8k) < 0.5, $"{at8k}");
        Assert.True(GainDb(SinePeak(eq, 30.0)) < -25.0); // 12 dB/octave, ~2.7 octaves down
        // The drawn curve agrees with what the filter does.
        foreach (double freq in new[] { 30.0, 200.0, 1000.0, 3000.0 })
        {
            var again = new ParametricEqEffect();
            again.SetParams(values);
            again.Prepare(Rate);
            double measured = GainDb(SinePeak(again, freq));
            Assert.True(Math.Abs(measured - ResponseDb(values, freq, Rate)) < 0.3, $"{freq}: {measured}");
        }
    }

    [Fact]
    public void EqGlidesAndSwitchesQuietly()
    {
        var eq = new ParametricEqEffect();
        eq.Prepare(Rate);
        // A steady sine; change gain, type and on/off mid-stream: the output
        // never jumps far beyond the boosted level, and stays finite.
        const int frames = 512 * 96; // about 1 s, in whole blocks
        var x = Signals.Sine(1000.0, 0.25, frames);
        var values = new Dictionary<string, double>();
        float previous = 0f;
        float worstStep = 0f;
        for (int start = 0; start < frames; start += 512)
        {
            if (start == 4096) { values["b3Gain"] = 12.0; eq.SetParams(values); }
            if (start == 16384) { values["b3Type"] = (double)BiquadType.HighShelf; eq.SetParams(values); }
            if (start == 32768) { values["b3On"] = 0.0; eq.SetParams(values); }
            eq.Process(x.AsSpan(start, 512));
            for (int i = start; i < start + 512; i++)
            {
                float v = x[i];
                Assert.True(float.IsFinite(v));
                worstStep = Math.Max(worstStep, Math.Abs(v - previous));
                previous = v;
            }
        }
        // A 1 kHz sine at 4x (+12 dB) moves at most ~0.13 per sample at 48 kHz.
        Assert.True(worstStep < 0.2f, $"{worstStep}");
    }

    [Fact]
    public void EqBandOffAndOnBringsBackNoOldSound()
    {
        var values = Signals.Params(("b3Gain", 24.0), ("b3Q", 10.0));
        var eq = new ParametricEqEffect();
        eq.SetParams(values);
        eq.Prepare(Rate);
        SinePeak(eq, 1000.0, Rate, 1.0);
        values["b3On"] = 0.0;
        eq.SetParams(values);
        eq.Process(new float[4800]); // fades out
        values["b3On"] = 1.0;
        eq.SetParams(values);
        var after = new float[4800];
        eq.Process(after);
        Assert.All(after, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void EqExtremesStayStable()
    {
        var values = AllBands(StudioCatalogue.EqBands, "Freq", 20000.0);
        for (int b = 1; b <= StudioCatalogue.EqBands; b++)
        {
            values[$"b{b}Gain"] = 24.0;
            values[$"b{b}Q"] = 10.0;
        }
        var eq = new ParametricEqEffect();
        eq.SetParams(values);
        eq.Prepare(44100.0); // 20 kHz is above 0.45 x 44.1 kHz: clamped
        Assert.True(double.IsFinite(SinePeak(eq, 15000.0, 44100.0)));
        // Below range: clamped.
        Assert.True(Biquad.Design(BiquadType.HighCut, 5.0, 0.0, 0.0, Rate).B0 > 0.0);
        // Parameters by kind.
        var info = StudioCatalogue.Find(StudioCatalogue.Eq)!;
        Assert.Equal(30, info.Params.Count);
        Assert.Equal(StudioParamKind.Choice, info.Params[1].Kind);
        Assert.Equal(5, info.Params[1].Options!.Count);
        Assert.Equal(3.0, info.Params[1].Valid(2.6));
        Assert.Equal(Biquad.MaxFreq, info.Params[2].Valid(1e9));
    }

    [Fact]
    public void EqDoesNotAllocate()
    {
        var eq = new ParametricEqEffect();
        eq.SetParams(Signals.Params(("b3Gain", 6.0), ("b1Type", (double)BiquadType.LowCut)));
        eq.Prepare(Rate);
        Assert.Equal(0, Signals.AllocatedBy(eq, Signals.Noise(0.5, 48000)));
    }

    [Fact]
    public void EqParameterChangesWhileRunningStayFinite()
    {
        var eq = new ParametricEqEffect();
        eq.Prepare(Rate);
        var x = Signals.Noise(0.5, 48000);
        var random = new Random(7);
        for (int start = 0; start < x.Length; start += 256)
        {
            var values = new Dictionary<string, double>();
            for (int b = 1; b <= StudioCatalogue.EqBands; b++)
            {
                values[$"b{b}On"] = random.Next(2);
                values[$"b{b}Type"] = random.Next(5);
                values[$"b{b}Freq"] = 20.0 * Math.Pow(1000.0, random.NextDouble());
                values[$"b{b}Gain"] = (random.NextDouble() * 48.0) - 24.0;
                values[$"b{b}Q"] = 0.1 * Math.Pow(100.0, random.NextDouble());
            }
            eq.SetParams(values);
            eq.Process(x.AsSpan(start, Math.Min(256, x.Length - start)));
        }
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void MultibandSumsFlat()
    {
        // Ratio 1:1 everywhere: nothing compresses, and the four bands add
        // back up to the input at every frequency, crossovers included.
        var values = AllBands(StudioCatalogue.MultibandBands, "Ratio", 1.0);
        var mb = new MultibandEffect();
        mb.SetParams(values);
        mb.Prepare(Rate);
        foreach (double freq in new[] { 40.0, 120.0, 400.0, 1000.0, 3000.0, 6000.0, 12000.0 })
            Assert.True(Math.Abs(GainDb(SinePeak(mb, freq))) < 0.1, $"{freq}");
        // Moved crossovers (out of order, even): still flat.
        values["x1"] = 500.0;
        values["x2"] = 400.0;
        values["x3"] = 2000.0;
        mb.SetParams(values);
        foreach (double freq in new[] { 400.0, 500.0, 2000.0 })
            Assert.True(Math.Abs(GainDb(SinePeak(mb, freq))) < 0.1, $"{freq}");
    }

    [Fact]
    public void MultibandCompressesOneBand()
    {
        var values = AllBands(StudioCatalogue.MultibandBands, "Ratio", 1.0);
        // Band 3: -20 dB threshold, 4:1. A 2 kHz tone peaking at -6 dBFS is
        // -9 dB RMS, 11 dB over: it comes out 11 x 3/4 = 8.3 dB down.
        // Band 3 made wide (200 Hz - 16 kHz) so the whole tone is in it.
        values["x2"] = 200.0;
        values["x3"] = 16000.0;
        values["b3Threshold"] = -20.0;
        values["b3Ratio"] = 4.0;
        var mb = new MultibandEffect();
        mb.SetParams(values);
        mb.Prepare(Rate);
        double loud = 20.0 * Math.Log10(SinePeak(mb, 2000.0, Rate, 0.5) / 0.5);
        Assert.True(Math.Abs(loud + 8.27) < 0.5, $"{loud}");
        var meters = new float[8];
        Assert.Equal(8, mb.ReadMeters(meters));
        Assert.True(Math.Abs(meters[2] - 8.27f) < 0.7f, $"{meters[2]}");
        Assert.Equal(0f, meters[0]);
        // Band 3's level: the tone's -9 dB RMS, as dB above the -80 dB floor;
        // band 1 only gets the onset's leakage through the filters, far down.
        Assert.True(Math.Abs(meters[6] - 71f) < 1.5f, $"{meters[6]}");
        Assert.True(meters[4] < 40f, $"{meters[4]}");
        // Another band's tone is left alone.
        Assert.True(Math.Abs(20.0 * Math.Log10(SinePeak(mb, 60.0, Rate, 0.5) / 0.5)) < 0.3);
        // Makeup on band 3 brings it back up.
        values["b3Makeup"] = 8.27;
        mb.SetParams(values);
        double made = 20.0 * Math.Log10(SinePeak(mb, 2000.0, Rate, 0.5) / 0.5);
        Assert.True(Math.Abs(made) < 0.5, $"{made}");
        // Solo band 1: the 2 kHz tone is gone.
        values["b1Solo"] = 1.0;
        mb.SetParams(values);
        Assert.True(GainDb(SinePeak(mb, 2000.0)) < -40.0);
        Assert.True(Math.Abs(GainDb(SinePeak(mb, 30.0))) < 0.3); // two octaves under x1: all band 1
        // Too short a span: nothing written.
        Assert.Equal(0, mb.ReadMeters(new float[7]));
    }

    [Fact]
    public void MultibandBandOffIsUncompressedAtOnce()
    {
        // A multiband band turned off is uncompressed within milliseconds,
        // whatever its release time.
        var values = new Dictionary<string, double>();
        for (int b = 1; b <= StudioCatalogue.MultibandBands; b++)
        {
            values[$"b{b}Threshold"] = -40.0;
            values[$"b{b}Ratio"] = 20.0;
            values[$"b{b}Release"] = 3000.0;
        }
        var mb = new MultibandEffect();
        mb.SetParams(values);
        mb.Prepare(Rate);
        Assert.True(GainDb(SinePeak(mb, 30.0, Rate, 0.5)) < -20.0);
        values["b1On"] = 0.0;
        mb.SetParams(values);
        mb.Process(new float[2400]); // 50 ms
        var meters = new float[8];
        mb.ReadMeters(meters);
        mb.ReadMeters(meters); // only what's new
        mb.Process(new float[480]);
        mb.ReadMeters(meters);
        Assert.True(meters[0] < 0.1f, $"{meters[0]}");
    }

    [Fact]
    public void CompressorCurve()
    {
        // Below the knee: unchanged; above: the ratio; the knee joins them.
        Assert.Equal(-40.0, StudioMath.CompressorCurve(-40.0, -20.0, 4.0));
        Assert.Equal(-16.0, StudioMath.CompressorCurve(-4.0, -20.0, 4.0));
        const double edge = StudioMath.KneeDb / 2.0;
        Assert.True(Math.Abs(StudioMath.CompressorCurve(-20.0 - edge, -20.0, 4.0) - (-20.0 - edge)) < 1e-9);
        Assert.True(Math.Abs(StudioMath.CompressorCurve(-20.0 + edge, -20.0, 4.0) - (-20.0 + (edge / 4.0))) < 1e-9);
        Assert.Equal(31, StudioCatalogue.Find(StudioCatalogue.Multiband)!.Params.Count);
    }

    [Fact]
    public void MultibandDoesNotAllocate()
    {
        var mb = new MultibandEffect();
        mb.Prepare(Rate); // -20 dB thresholds, 2:1: white noise at 0.5 compresses band 4
        Assert.Equal(0, Signals.AllocatedBy(mb, Signals.Noise(0.5, 48000)));
        var meters = new float[8];
        mb.ReadMeters(meters);
        Assert.True(meters[3] > 0f, $"{meters[3]}");
    }

    [Fact]
    public void MultibandParameterChangesWhileRunningStayFinite()
    {
        var mb = new MultibandEffect();
        mb.Prepare(Rate);
        var x = Signals.Noise(0.5, 48000);
        var random = new Random(7);
        for (int start = 0; start < x.Length; start += 256)
        {
            var values = new Dictionary<string, double>();
            for (int i = 1; i <= 3; i++) values[$"x{i}"] = 30.0 * Math.Pow(16000.0 / 30.0, random.NextDouble());
            for (int b = 1; b <= StudioCatalogue.MultibandBands; b++)
            {
                values[$"b{b}On"] = random.Next(2);
                values[$"b{b}Solo"] = random.Next(2);
                values[$"b{b}Threshold"] = -60.0 * random.NextDouble();
                values[$"b{b}Ratio"] = 1.0 + (19.0 * random.NextDouble());
                values[$"b{b}Attack"] = 0.1 + (199.9 * random.NextDouble());
                values[$"b{b}Release"] = 10.0 + (2990.0 * random.NextDouble());
                values[$"b{b}Makeup"] = -12.0 + (36.0 * random.NextDouble());
            }
            mb.SetParams(values);
            mb.Process(x.AsSpan(start, Math.Min(256, x.Length - start)));
        }
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
    }
}
