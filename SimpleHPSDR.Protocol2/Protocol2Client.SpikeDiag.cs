// SPDX-License-Identifier: GPL-2.0-or-later
//
// Diagnostic for short spikes in the receive IQ, off unless
// SIMPLEHPSDR_SPIKE_DIAG=1. It reads the raw 24-bit words of every DDC packet
// before anything else touches them, and logs each spike with the time since
// the last datagram sent to each of the radio's ports, so a spike caused by a
// command (or by transmit IQ / speaker audio) shows as a fixed delay.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SimpleHPSDR.Protocol2;

public sealed partial class Protocol2Client
{
    private static readonly bool SpikeDiagOn =
        Environment.GetEnvironmentVariable("SIMPLEHPSDR_SPIKE_DIAG") == "1";

    // Ports 1024 (general), 1025 (receive), 1026 (transmit), 1027 (high
    // priority), 1028 (speaker audio), 1029 (transmit IQ).
    private readonly long[] _spikeDiagLastSendTicks = new long[6];
    private readonly SpikeDiagDdc[] _spikeDiagDdc = new SpikeDiagDdc[MaxRxDdc];
    private long _spikeDiagWindowStart;
    private int _spikeDiagLinesInWindow;

    private sealed class SpikeDiagDdc
    {
        public double Level;        // slow mean of |word|, spikes left out
        public long Packets;
        public long Events;
        public long IOnly;
        public long QOnly;
        public long Both;
        public long LastSummaryTicks;
        public uint LastSeq;
        public bool HaveSeq;
        public long SeqGaps;
    }

    private void SpikeDiagNoteSend(int port)
    {
        int slot = port - 1024;
        if ((uint)slot < (uint)_spikeDiagLastSendTicks.Length)
            Volatile.Write(ref _spikeDiagLastSendTicks[slot], Stopwatch.GetTimestamp());
    }

    private static int SpikeDiagWord(byte[] buf, int off)
    {
        int v = (buf[off] << 16) | (buf[off + 1] << 8) | buf[off + 2];
        return (v & 0x800000) != 0 ? v | unchecked((int)0xFF000000) : v;
    }

    private void SpikeDiagScan(byte[] buf, int ddcIndex, uint seq, bool paired)
    {
        if ((uint)ddcIndex >= (uint)_spikeDiagDdc.Length) return;
        var d = _spikeDiagDdc[ddcIndex] ??= new SpikeDiagDdc { LastSummaryTicks = Stopwatch.GetTimestamp() };
        d.Packets++;
        if (d.HaveSeq && seq != unchecked(d.LastSeq + 1)) d.SeqGaps++;
        d.HaveSeq = true;
        d.LastSeq = seq;

        // A paired packet interleaves two ADCs; scan it as the wire has it
        // (stride 6 over all 238 words) so both are covered.
        const int n = DiscoverySamplesPerPacket;
        const double minAbs = 8388608.0 * 0.002;
        double level = d.Level;
        if (level <= 0)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += Math.Abs(SpikeDiagWord(buf, 16 + i * 6)) + Math.Abs(SpikeDiagWord(buf, 19 + i * 6));
            level = sum / (2 * n);
        }

        int skipUntil = -1;
        for (int i = 0; i < n; i++)
        {
            int off = 16 + i * 6;
            int iw = SpikeDiagWord(buf, off), qw = SpikeDiagWord(buf, off + 3);
            int ia = Math.Abs(iw), qa = Math.Abs(qw);
            double limit = Math.Max(8 * level, minAbs);
            bool iHit = ia > limit, qHit = qa > limit;
            if (!iHit && !qHit)
            {
                level += ((ia + qa) * 0.5 - level) * (1.0 / 4096);
                continue;
            }
            if (i <= skipUntil) continue;

            // One event: the largest word within the next 12 samples.
            int peak = i, peakAbs = Math.Max(ia, qa);
            bool anyI = iHit, anyQ = qHit;
            int end = Math.Min(n - 1, i + 12);
            for (int k = i + 1; k <= end; k++)
            {
                int ka = Math.Abs(SpikeDiagWord(buf, 16 + k * 6)), kq = Math.Abs(SpikeDiagWord(buf, 19 + k * 6));
                if (ka > limit) anyI = true;
                if (kq > limit) anyQ = true;
                if (Math.Max(ka, kq) > peakAbs) { peakAbs = Math.Max(ka, kq); peak = k; }
            }
            skipUntil = end;
            d.Events++;
            if (anyI && anyQ) d.Both++;
            else if (anyI) d.IOnly++;
            else d.QOnly++;
            SpikeDiagLog(buf, ddcIndex, seq, paired, peak, level, anyI, anyQ);
        }
        d.Level = level;

        long now = Stopwatch.GetTimestamp();
        if (now - d.LastSummaryTicks >= 10 * Stopwatch.Frequency)
        {
            double secs = (now - d.LastSummaryTicks) / (double)Stopwatch.Frequency;
            _log.LogInformation(
                "p2.spike.sum ddc={Ddc} secs={Secs:F1} packets={Packets} events={Events} iOnly={IOnly} qOnly={QOnly} both={Both} seqGaps={Gaps} level={Level:F6} rateKhz={Rate} keyed={Keyed}",
                ddcIndex, secs, d.Packets, d.Events, d.IOnly, d.QOnly, d.Both, d.SeqGaps,
                level / 8388608.0, _sampleRateKhz, _moxOn || _tuneActive);
            d.Packets = d.Events = d.IOnly = d.QOnly = d.Both = d.SeqGaps = 0;
            d.LastSummaryTicks = now;
        }
    }

    private void SpikeDiagLog(byte[] buf, int ddcIndex, uint seq, bool paired, int peak, double level, bool anyI, bool anyQ)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _spikeDiagWindowStart >= Stopwatch.Frequency)
        {
            _spikeDiagWindowStart = now;
            _spikeDiagLinesInWindow = 0;
        }
        if (++_spikeDiagLinesInWindow > 60) return;

        const int n = DiscoverySamplesPerPacket;
        int from = Math.Max(0, peak - 24), to = Math.Min(n - 1, peak + 24);
        var iText = new System.Text.StringBuilder();
        var qText = new System.Text.StringBuilder();
        for (int k = from; k <= to; k++)
        {
            if (k > from) { iText.Append(' '); qText.Append(' '); }
            iText.Append(SpikeDiagWord(buf, 16 + k * 6));
            qText.Append(SpikeDiagWord(buf, 19 + k * 6));
        }
        double ms = 1000.0 / Stopwatch.Frequency;
        var since = new System.Text.StringBuilder();
        for (int s = 0; s < _spikeDiagLastSendTicks.Length; s++)
        {
            long t = Volatile.Read(ref _spikeDiagLastSendTicks[s]);
            if (s > 0) since.Append(' ');
            since.Append(1024 + s).Append('=');
            since.Append(t == 0 ? "-" : ((now - t) * ms).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }
        _log.LogInformation(
            "p2.spike ddc={Ddc} seq={Seq} paired={Paired} idx={Idx} first={First} kind={Kind} level={Level:F0} tMs={T:F1} keyed={Keyed} sinceSendMs=[{Since}] I=[{I}] Q=[{Q}]",
            ddcIndex, seq, paired, peak, from,
            anyI && anyQ ? "IQ" : anyI ? "I" : "Q",
            level, now * ms, _moxOn || _tuneActive, since.ToString(), iText.ToString(), qText.ToString());
    }
}
