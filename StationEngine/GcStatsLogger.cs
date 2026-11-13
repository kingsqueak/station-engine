// SPDX-License-Identifier: GPL-2.0-or-later
// SimpleHPSDR fork: opt-in garbage-collection figures for allocation checks.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace SimpleHPSDR.StationEngine;

/// <summary>
/// STATION_ENGINE_GC_STATS=1: every 10 s logs collections per generation,
/// bytes allocated per second and the GC pause time in that window
/// (log category SimpleHPSDR.StationEngine.GcStatsLogger, line "gc.stats").
/// </summary>
internal sealed class GcStatsLogger(ILogger<GcStatsLogger> log) : BackgroundService
{
    public static bool Enabled => Environment.GetEnvironmentVariable("STATION_ENGINE_GC_STATS") == "1";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var sampler = new AllocSampler();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long bytes = GC.GetTotalAllocatedBytes();
        var pause = GC.GetTotalPauseDuration();
        var sw = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
            long nb = GC.GetTotalAllocatedBytes();
            var np = GC.GetTotalPauseDuration();
            double s = sw.Elapsed.TotalSeconds;
            var info = GC.GetGCMemoryInfo();
            log.LogInformation(
                "gc.stats gen0={Gen0} gen1={Gen1} gen2={Gen2} allocKBps={AllocKBps:F0} pauseMs={PauseMs:F1} heapMB={HeapMB:F1} lohMB={LohMB:F1}",
                n0 - g0, n1 - g1, n2 - g2, (nb - bytes) / 1024.0 / s, (np - pause).TotalMilliseconds,
                info.HeapSizeBytes / 1048576.0,
                info.GenerationInfo.Length > 3 ? info.GenerationInfo[3].SizeAfterBytes / 1048576.0 : 0);
            var top = sampler.TakeTop(8);
            if (top.Length > 0)
                log.LogInformation("gc.alloc top types (KB/s, sampled): {Top}",
                    string.Join(", ", top.Select(t => $"{t.Type} {t.Bytes / 1024.0 / s:F0}")));
            (g0, g1, g2, bytes, pause) = (n0, n1, n2, nb, np);
            sw.Restart();
        }
    }
}

/// <summary>The runtime's GCAllocationTick event: one sample about every 100 KB
/// allocated, naming the type that crossed it. Summed by type.</summary>
internal sealed class AllocSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);   // GC keyword
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
        int iName = e.PayloadNames?.IndexOf("TypeName") ?? -1;
        int iAmt = e.PayloadNames?.IndexOf("AllocationAmount64") ?? -1;
        if (iName < 0 || iAmt < 0) return;
        string type = e.Payload![iName] as string ?? "?";
        long amount = Convert.ToInt64(e.Payload[iAmt]);
        _bytes.AddOrUpdate(type, amount, (_, v) => v + amount);
    }

    public (string Type, long Bytes)[] TakeTop(int n)
    {
        var snap = _bytes.ToArray();
        _bytes.Clear();
        return snap.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value)).ToArray();
    }
}
