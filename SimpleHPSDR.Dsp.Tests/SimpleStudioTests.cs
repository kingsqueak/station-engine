// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleHPSDR.Contracts;
using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

/// <summary>The chain as the engine uses it: SimpleStudio.Configure from a
/// control thread, Process block by block.</summary>
public class SimpleStudioTests
{
    private const int Rate = 48000;
    private const int Block = 256;

    private static StudioEffectConfig Fx(string id, string type, bool on = true,
        params (string Name, double Value)[] values) =>
        new(id, type, on, Signals.Params(values));

    private static SimpleStudio Studio(bool enabled, params StudioEffectConfig[] effects)
    {
        var studio = new SimpleStudio();
        studio.SetRate(Rate);
        studio.Configure(new SimpleStudioConfig(enabled, effects));
        return studio;
    }

    private static void Run(SimpleStudio studio, Span<float> signal)
    {
        for (int start = 0; start + Block <= signal.Length; start += Block)
            studio.Process(signal.Slice(start, Block), Rate);
    }

    private static StudioEffectConfig[] AllTen() =>
        [.. StudioCatalogue.Effects.Select(e => Fx(e.Type + "-x", e.Type))];

    [Fact]
    public void EveryCatalogueEffectCanBeMade()
    {
        foreach (var info in StudioCatalogue.Effects)
        {
            var effect = StudioChain.Create(info.Type);
            Assert.NotNull(effect);
            Assert.Equal(info.Type, effect!.Type);
        }
        Assert.Equal(10, StudioCatalogue.Effects.Count);
    }

    [Fact]
    public void OffOrEmptyIsNotWanted()
    {
        Assert.False(Studio(false, Fx("g", StudioCatalogue.Gain)).Wanted);
        Assert.False(Studio(true).Wanted);
        Assert.True(Studio(true, Fx("g", StudioCatalogue.Gain)).Wanted);
    }

    [Fact]
    public void EveryEffectBypassedLeavesTheBlockUntouched()
    {
        var studio = Studio(true, [.. StudioCatalogue.Effects.Select(e => Fx(e.Type, e.Type, on: false))]);
        var x = Signals.Noise(0.5, 48000);
        var before = (float[])x.Clone();
        Run(studio, x);
        Assert.Equal(before, x);
    }

    [Fact]
    public void NormalizedDropsWhatItDoesNotKnowAndFixesTheRest()
    {
        var many = Enumerable.Range(0, 12).Select(_ => Fx("same", StudioCatalogue.Gain,
            values: [("gainDb", 99.0), ("nonsense", 1.0)])).ToList();
        many.Insert(0, Fx("x", "no-such-effect"));
        var valid = new SimpleStudioConfig(true, many).Normalized();
        Assert.Equal(StudioCatalogue.MaxEffects, valid.Effects!.Count);
        Assert.Equal(valid.Effects.Count, valid.Effects.Select(e => e.Id).Distinct().Count());
        Assert.All(valid.Effects, e =>
        {
            Assert.Equal(StudioCatalogue.Gain, e.Type);
            Assert.Equal(24.0, e.Params!["gainDb"]);
            Assert.False(e.Params.ContainsKey("nonsense"));
        });
        var eq = new SimpleStudioConfig(true, [new StudioEffectConfig("", StudioCatalogue.Eq, true, null)]).Normalized();
        Assert.Equal(30, eq.Effects![0].Params!.Count);
        Assert.NotEqual("", eq.Effects[0].Id);
    }

    [Fact]
    public void ConfigSurvivesJson()
    {
        var config = new SimpleStudioConfig(true, AllTen()).Normalized();
        string json = System.Text.Json.JsonSerializer.Serialize(config);
        var back = System.Text.Json.JsonSerializer.Deserialize<SimpleStudioConfig>(json)!.Normalized();
        Assert.Equal(config.Effects!.Count, back.Effects!.Count);
        for (int i = 0; i < back.Effects.Count; i++)
        {
            Assert.Equal(config.Effects[i].Id, back.Effects[i].Id);
            Assert.Equal(config.Effects[i].Params!.OrderBy(p => p.Key), back.Effects[i].Params!.OrderBy(p => p.Key));
        }
    }

    [Fact]
    public void GainInTheChainSetsTheLevel()
    {
        var studio = Studio(true, Fx("g", StudioCatalogue.Gain, values: ("gainDb", -12.0)));
        var x = Signals.Sine(1000, 0.5, 48000);
        Run(studio, x);
        Assert.Equal(Signals.Db(0.5) - 12.0, Signals.Db(Signals.Peak(x.AsSpan(24000, 16384))), 1);
    }

    [Fact]
    public void SwitchingAnEffectCrossfades()
    {
        // -24 dB of gain switched off mid-signal: no jump bigger than the
        // sine's own steps plus the 10 ms fade.
        var studio = Studio(true, Fx("g", StudioCatalogue.Gain, values: ("gainDb", -24.0)));
        var x = Signals.Sine(100, 0.5, 48000);
        Run(studio, x.AsSpan(0, 24000 - (24000 % Block)));
        studio.Configure(new SimpleStudioConfig(true, [Fx("g", StudioCatalogue.Gain, on: false, ("gainDb", -24.0))]));
        int from = 24000 - (24000 % Block);
        Run(studio, x.AsSpan(from));
        double biggest = 0.0;
        for (int i = from - 100; i < from + 2000; i++) biggest = Math.Max(biggest, Math.Abs(x[i] - x[i - 1]));
        Assert.True(biggest < 0.02, $"step of {biggest}");
        Assert.Equal(0.5, Signals.Peak(x.AsSpan(from + 4800, 4800)), 2);
    }

    [Fact]
    public void SwitchingTheWholeChainCrossfades()
    {
        // As the engine drives it: Process only while Wanted.
        var on = new SimpleStudioConfig(true, [Fx("g", StudioCatalogue.Gain, values: ("gainDb", -24.0))]);
        var studio = new SimpleStudio();
        studio.SetRate(Rate);
        studio.Configure(on with { Enabled = false });
        var x = Signals.Sine(100, 0.5, 48000);
        var before = (float[])x.Clone();
        int blocks = x.Length / Block;
        for (int b = 0; b < blocks; b++)
        {
            if (b == 40) studio.Configure(on);
            if (b == 120) studio.Configure(on with { Enabled = false });
            var block = x.AsSpan(b * Block, Block);
            if (studio.Wanted) studio.Process(block, Rate); else studio.NoteSkipped();
        }
        double biggest = 0.0;
        for (int i = 1; i < blocks * Block; i++) biggest = Math.Max(biggest, Math.Abs(x[i] - x[i - 1]));
        Assert.True(biggest < 0.02, $"step of {biggest}");
        Assert.Equal(before.AsSpan(0, 40 * Block).ToArray(), x.AsSpan(0, 40 * Block).ToArray());          // off: untouched
        Assert.Equal(0.5 * Math.Pow(10, -24 / 20.0), Signals.Peak(x.AsSpan(80 * Block, 20 * Block)), 3);   // on
        Assert.Equal(before.AsSpan(125 * Block, 20 * Block).ToArray(), x.AsSpan(125 * Block, 20 * Block).ToArray()); // off again, after the fade
        Assert.False(studio.Wanted);
    }

    [Fact]
    public void RearrangingKeepsTheEffectsRunning()
    {
        // A gate that has opened stays open when the chain is rearranged
        // around it: a new gate would start closed and cut the first samples.
        var gate = Fx("gate", StudioCatalogue.Gate, values: ("threshold", -40.0));
        var gain = Fx("gain", StudioCatalogue.Gain);
        var studio = Studio(true, gate, gain);
        var x = Signals.Sine(1000, 0.5, 48000);
        int half = 24000 - (24000 % Block);
        Run(studio, x.AsSpan(0, half));
        studio.Configure(new SimpleStudioConfig(true, [gain, gate]));
        Run(studio, x.AsSpan(half));
        Assert.Equal(0.5, Signals.Peak(x.AsSpan(half, Block)), 2);

        // The same gate under another id is a new one: it starts closed.
        var other = Studio(true, gate);
        var y = Signals.Sine(1000, 0.5, 48000);
        Run(other, y.AsSpan(0, half));
        other.Configure(new SimpleStudioConfig(true, [gate with { Id = "another" }]));
        other.Process(y.AsSpan(half, 8), Rate);
        Assert.True(Signals.Peak(y.AsSpan(half, 8)) < 0.1);
    }

    [Fact]
    public void AfterAPauseNothingFromBeforeComesOut()
    {
        var studio = Studio(true, Fx("r", StudioCatalogue.Reverb, values: [("mix", 100.0), ("decay", 5.0)]));
        var loud = Signals.Noise(0.5, 48000);
        Run(studio, loud);
        studio.NoteSkipped();
        var quiet = new float[4800];
        Run(studio, quiet);
        Assert.Equal(0.0, Signals.Peak(quiet), 6);
    }

    [Fact]
    public void ABrokenSampleDoesNotStick()
    {
        var studio = Studio(true, AllTen());
        var x = Signals.Sine(1000, 0.25, 48000);
        x[1000] = float.NaN;
        x[1001] = float.PositiveInfinity;
        Run(studio, x);
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
        Assert.True(Signals.Rms(x.AsSpan(40000, 4096)) > 0.01);
    }

    [Fact]
    public void AllTenRunWithoutAllocatingAndFastEnough()
    {
        var studio = Studio(true, AllTen());
        var x = Signals.Noise(0.5, 48000 * 4);
        var work = new float[x.Length];
        void Pass()
        {
            x.CopyTo(work, 0);
            Run(studio, work);
        }
        Assert.Equal(0, Signals.LeastAllocated(Pass));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Pass();
        clock.Stop();
        double audioSeconds = x.Length / (double)Rate;
        // All ten effects must take well under a tenth of real time even in a
        // debug build on a slow machine.
        Console.WriteLine($"SimpleStudio, all ten effects: {clock.Elapsed.TotalMilliseconds:F1} ms for {audioSeconds:F1} s of audio");
        Assert.True(clock.Elapsed.TotalSeconds < audioSeconds * 0.1,
            $"{clock.Elapsed.TotalMilliseconds:F1} ms for {audioSeconds:F1} s of audio");
    }

    [Fact]
    public void MetersReadOnlyWhileRunning()
    {
        var studio = Studio(true, Fx("c", StudioCatalogue.Compressor), Fx("g", StudioCatalogue.Gain, values: ("gainDb", -6.0)));
        Assert.False(studio.ReadMeters().Active); // nothing processed yet; arms the measurement
        var x = Signals.Sine(1000, 0.5, 48000);
        Run(studio, x);
        var meters = studio.ReadMeters();
        Assert.True(meters.Active);
        Assert.Equal(2, meters.Effects.Count);
        Assert.Equal("c", meters.Effects[0].Id);
        Assert.Equal(Signals.Db(0.5), meters.Effects[0].InPeakDb, 0);
        Assert.Equal(2, meters.Effects[0].Values.Count);
        Assert.True(meters.Effects[0].Values[0] > 1.0); // compressing
        Assert.Equal(meters.Effects[0].OutRmsDb, meters.Effects[1].InRmsDb, 6);
        Assert.Equal(meters.Effects[1].InRmsDb - 6.0, meters.Effects[1].OutRmsDb, 0);
    }

    [Fact]
    public void AChainForAnotherRatePassesTheMicThrough()
    {
        var studio = Studio(true, Fx("g", StudioCatalogue.Gain, values: ("gainDb", -24.0)));
        var x = Signals.Sine(1000, 0.5, Block);
        var before = (float[])x.Clone();
        studio.Process(x, 96000);
        Assert.Equal(before, x);
    }

    [Fact]
    public async Task SettingsChangeFromAnotherThreadWhileRunning()
    {
        var studio = Studio(true, AllTen());
        var random = new Random(3);
        using var stop = new CancellationTokenSource();
        var control = Task.Run(() =>
        {
            var types = StudioCatalogue.Effects;
            while (!stop.IsCancellationRequested)
            {
                var effects = new List<StudioEffectConfig>();
                foreach (var info in types.OrderBy(_ => random.Next()).Take(random.Next(1, 9)))
                {
                    var values = new Dictionary<string, double>();
                    foreach (var p in info.Params)
                        values[p.Name] = p.Min + (random.NextDouble() * (p.Max - p.Min));
                    effects.Add(new StudioEffectConfig(info.Type, info.Type, random.Next(4) != 0, values));
                }
                studio.Configure(new SimpleStudioConfig(true, effects));
                studio.ReadMeters();
            }
        });
        var x = Signals.Noise(0.5, Block);
        var block = new float[Block];
        for (int n = 0; n < 6000; n++)
        {
            x.CopyTo(block, 0);
            if (studio.Wanted) studio.Process(block, Rate);
            foreach (float v in block) Assert.True(float.IsFinite(v));
        }
        stop.Cancel();
        await control;
    }
}
