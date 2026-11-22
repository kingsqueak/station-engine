// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris

using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Tests;

/// <summary>Test signals and measurements shared by the SimpleStudio tests.</summary>
internal static class Signals
{
    public const double Rate = 48000.0;

    public static float[] Sine(double freq, double amplitude, int samples, double rate = Rate)
    {
        var x = new float[samples];
        for (int i = 0; i < samples; i++)
            x[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * freq * i / rate));
        return x;
    }

    /// <summary>White noise, the same every run.</summary>
    public static float[] Noise(double amplitude, int samples, int seed = 1)
    {
        var random = new Random(seed);
        var x = new float[samples];
        for (int i = 0; i < samples; i++)
            x[i] = (float)(amplitude * ((random.NextDouble() * 2.0) - 1.0));
        return x;
    }

    public static double Rms(ReadOnlySpan<float> x)
    {
        double sum = 0.0;
        foreach (float v in x) sum += (double)v * v;
        return x.Length == 0 ? 0.0 : Math.Sqrt(sum / x.Length);
    }

    public static double Peak(ReadOnlySpan<float> x)
    {
        double peak = 0.0;
        foreach (float v in x) peak = Math.Max(peak, Math.Abs(v));
        return peak;
    }

    public static double Db(double level) => 20.0 * Math.Log10(Math.Max(level, 1e-12));

    /// <summary>Runs an effect over a signal in blocks, as the engine does.</summary>
    public static void Run(StudioEffect effect, Span<float> signal, int block = 256)
    {
        for (int start = 0; start < signal.Length; start += block)
            effect.Process(signal.Slice(start, Math.Min(block, signal.Length - start)));
    }

    public static Dictionary<string, double> Params(params (string Name, double Value)[] values)
    {
        var d = new Dictionary<string, double>();
        foreach (var (name, value) in values) d[name] = value;
        return d;
    }

    /// <summary>Bytes allocated on this thread while running an effect over a
    /// signal: Process must not allocate. The smallest of several passes, since
    /// the runtime itself now and then allocates on the calling thread (the
    /// JIT moving a method up a tier); an effect that allocates does so on
    /// every pass.</summary>
    public static long AllocatedBy(StudioEffect effect, float[] signal, int block = 256)
    {
        var work = new float[signal.Length];
        return LeastAllocated(() =>
        {
            signal.CopyTo(work, 0);
            for (int start = 0; start + block <= work.Length; start += block)
                effect.Process(work.AsSpan(start, block));
        });
    }

    public static long LeastAllocated(Action pass, int passes = 5)
    {
        pass(); // warm up
        long least = long.MaxValue;
        for (int n = 0; n < passes; n++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            pass();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }
}
