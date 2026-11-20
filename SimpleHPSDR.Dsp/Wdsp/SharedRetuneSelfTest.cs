// SPDX-License-Identifier: GPL-2.0-or-later
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Wdsp;

/// <summary>
/// No-radio check of the retune of a shared stream (StationEngine
/// --selftest-shared-retune). Two channels are fed the same synthetic
/// carrier, started at different points so their block grids differ, as a
/// receiver and one on its stream are. The source's centre then steps by
/// 20 kHz at one of the source's block boundaries, as the radio's does on a
/// block-aligned retune. The follower's dial does not move, so its 1 kHz
/// tone should carry on untouched: no gap, no step in level or phase.
/// Run twice: with the follower's shift riding on the retune, and changed at
/// once (how it was before), which must show the disturbance, so the check
/// is known to see it.
/// </summary>
public static class SharedRetuneSelfTest
{
    private const int Rate = 384_000;
    private const int Frame = 512;
    private const int BlockFrames = 8;
    private const int StepHz = 20_000;
    private const double ToneHz = 1000.0;
    private const int Window = 96;          // 2 ms of audio at 48 kHz: two cycles of the tone

    public static int Run(TextWriter output)
    {
        var bridged = RunCase(bridge: true, output);
        var atOnce = RunCase(bridge: false, output);
        bool ok = bridged.Valid && atOnce.Valid
            && bridged.DisturbedMs <= 2.0 && Math.Abs(bridged.PhaseStepRad) < 0.1
            && atOnce.DisturbedMs >= 4.0;
        output.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private readonly record struct Result(bool Valid, double DisturbedMs, double PhaseStepRad);

    private static Result RunCase(bool bridge, TextWriter output)
    {
        string label = bridge ? "shift rides on the retune" : "shift changed at once";
        using var engine = new WdspDspEngine { BlockingIqFeed = true };
        int source = engine.OpenChannel(Rate, 1024);
        int follower = engine.OpenChannel(Rate, 1024);
        foreach (int ch in (int[])[source, follower])
        {
            engine.SetMode(ch, RxMode.USB);
            engine.SetFilter(ch, 150, 2850);
            engine.SetAgc(ch, new AgcConfig(AgcMode.Fixed));
        }
        // Source dial 50 kHz above the centre, follower's 30 kHz above; the
        // carrier 1 kHz above the follower's dial.
        const int sourceShift = 50_000, followerShift = 30_000;
        engine.SetCtunShift(source, sourceShift);
        engine.SetCtunShift(follower, followerShift);

        var audio = new List<float>(96_000);
        var readBuf = new float[4800];
        var scrap = new float[4800];
        var piece = new double[2 * 238];    // a Protocol 2 packet's worth
        double phase = 0;
        long pos = 0;
        long stepAt = long.MaxValue;
        long sourceFrom = 3 * Frame + 137;   // the follower starts this much earlier
        long sourceFed = 0;

        // paced: a packet at a time with the workers keeping up, as on a live
        // stream, where a retune is placed about a frame ahead of the samples.
        void Feed(long samples, bool paced = false)
        {
            long end = pos + samples;
            while (pos < end)
            {
                int n = (int)Math.Min(238, end - pos);
                for (int i = 0; i < n; i++)
                {
                    double hz = followerShift + ToneHz - (pos + i >= stepAt ? StepHz : 0);
                    // The engine's IQ runs the other way round: a signal
                    // above the centre turns clockwise (WDSP's shift by
                    // dial - centre then brings it up to baseband).
                    piece[2 * i] = 0.01 * Math.Cos(phase);
                    piece[2 * i + 1] = -0.01 * Math.Sin(phase);
                    phase += 2 * Math.PI * hz / Rate;
                    if (phase > Math.PI) phase -= 2 * Math.PI;
                }
                var span = piece.AsSpan(0, 2 * n);
                if (pos >= sourceFrom)
                {
                    engine.FeedIq(source, span, pos);
                    sourceFed += n;
                }
                else if (pos + n > sourceFrom)
                {
                    int skip = (int)(sourceFrom - pos);
                    engine.FeedIq(source, span[(2 * skip)..], sourceFrom);
                    sourceFed += n - skip;
                }
                engine.FeedIq(follower, span, pos);
                pos += n;
                if (paced) Thread.Sleep(1);
                else if (pos % (238 * 40) == 0) Drain();
            }
        }

        void Drain()
        {
            Thread.Sleep(15);
            int got;
            while ((got = engine.ReadAudio(follower, readBuf)) > 0)
                for (int i = 0; i < got; i++) audio.Add(readBuf[i]);
            while (engine.ReadAudio(source, scrap) > 0) { }
        }

        // A long run first: WDSP's own oscillator is then far from where it
        // started, so a hand-over that lost the phase would show.
        Feed(238 * 1000);
        Thread.Sleep(300);
        Drain();
        long exchanged = sourceFed / Frame;
        long pick = exchanged + 1;
        while (pick % BlockFrames != BlockFrames - 1) pick++;
        stepAt = sourceFrom + Frame * (pick + 1);
        bool scheduled = bridge
            ? engine.TryScheduleAlignedRetune(source, sourceShift - StepHz, 1.2, static () => { },
                [(follower, followerShift - StepHz)])
            : engine.TryScheduleAlignedRetune(source, sourceShift - StepHz, 1.2, static () => { });
        if (!bridge) engine.SetCtunShift(follower, followerShift - StepHz);
        Feed(238 * 120, paced: true);
        Feed(238 * 680);
        Thread.Sleep(300);
        Drain();
        long placed = engine.LastAlignedRetunePos(source);
        engine.CloseChannel(follower);
        engine.CloseChannel(source);

        // The tone's level and phase in consecutive 2 ms windows.
        int windows = audio.Count / Window;
        var level = new double[windows];
        var angle = new double[windows];
        for (int w = 0; w < windows; w++)
        {
            double re = 0, im = 0;
            for (int i = 0; i < Window; i++)
            {
                double a = 2 * Math.PI * ToneHz * i / 48_000.0;
                re += audio[w * Window + i] * Math.Cos(a);
                im -= audio[w * Window + i] * Math.Sin(a);
            }
            level[w] = Math.Sqrt(re * re + im * im) * 2 / Window;
            angle[w] = Math.Atan2(im, re);
        }
        int settle = 150;   // 300 ms for the channel to settle
        if (windows < settle + 200 || !scheduled || placed != stepAt)
        {
            output.WriteLine($"{label}: not measured (windows={windows} scheduled={scheduled} " +
                $"placed={placed} expected={stepAt})");
            return new Result(false, 0, 0);
        }
        int stepWindow = (int)(stepAt * 48_000L / Rate / Window);
        double refLevel = Median(level, settle, stepWindow - 20);
        double before = CircularMean(angle, settle, stepWindow - 20);
        double after = CircularMean(angle, windows - 100, windows);
        int disturbed = 0;
        for (int w = settle; w < windows; w++)
        {
            double off = Math.Min(Wrap(angle[w] - before), Wrap(angle[w] - after));
            if (Math.Abs(level[w] / refLevel - 1) > 0.1 || off > 0.15) disturbed++;
        }
        double stepRad = Math.Atan2(Math.Sin(after - before), Math.Cos(after - before));
        output.WriteLine($"{label}: tone level {refLevel:E2}, disturbed {disturbed * 2.0:0.0} ms, " +
            $"phase step {stepRad:0.000} rad (retune at sample {stepAt})");
        return new Result(refLevel > 1e-6, disturbed * 2.0, stepRad);
    }

    private static double Wrap(double rad) => Math.Abs(Math.Atan2(Math.Sin(rad), Math.Cos(rad)));

    private static double Median(double[] v, int from, int to)
    {
        var part = v[from..to];
        Array.Sort(part);
        return part[part.Length / 2];
    }

    private static double CircularMean(double[] v, int from, int to)
    {
        double s = 0, c = 0;
        for (int i = from; i < to; i++) { s += Math.Sin(v[i]); c += Math.Cos(v[i]); }
        return Math.Atan2(s, c);
    }
}
