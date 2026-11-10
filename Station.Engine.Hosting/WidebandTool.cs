// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR fork of Station Engine.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

namespace SimpleHPSDR.Server;

/// <summary>
/// The wideband tool: a third, independent consumer of the Protocol 2 raw-ADC
/// snapshots (after the operator wideband display and the public overview),
/// for a separate 0-60 MHz window in the app. Own analyzer and buffers; never
/// reads or writes the operator's display, zoom or wideband centre. The app
/// polls <c>GET /api/wideband/tool</c>; each poll keeps the radio's wideband
/// transport running for <see cref="DemandHold"/>, so closing the window (or
/// the app) lets it stop by itself.
/// </summary>
internal sealed class WidebandTool
{
    public const int MaxFramesPerSecond = 10;
    public static readonly TimeSpan DemandHold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromSeconds(1.0 / MaxFramesPerSecond);

    private readonly TimeProvider _time;
    private readonly WidebandSpectrumAnalyzer _analyzer = new();
    private readonly float[] _panWork = new float[WidebandSpectrumAnalyzer.DisplayWidth];
    private readonly float[] _wfWork = new float[WidebandSpectrumAnalyzer.DisplayWidth];

    private readonly object _gate = new();
    private readonly float[] _pan = new float[WidebandSpectrumAnalyzer.DisplayWidth];
    private readonly float[] _wf = new float[WidebandSpectrumAnalyzer.DisplayWidth];
    private long _seq;
    private long _centerHz = WidebandSpectrumAnalyzer.DisplayCenterHz;
    private float _hzPerPixel = WidebandSpectrumAnalyzer.HzPerPixel;
    private long _lastDemand = long.MinValue;

    // Analyzer-worker only.
    private long _lastFrame;
    private bool _framed;

    public WidebandTool(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Called by each poll from the app.</summary>
    public void Demand()
    {
        lock (_gate) _lastDemand = _time.GetTimestamp();
    }

    /// <summary>True while a poll arrived within <see cref="DemandHold"/>.</summary>
    public bool IsDemanded
    {
        get
        {
            lock (_gate)
                return _lastDemand != long.MinValue &&
                       _time.GetElapsedTime(_lastDemand) < DemandHold;
        }
    }

    /// <summary>
    /// Analyse one snapshot when the frame budget allows. While the radio is
    /// transmitting nothing is analysed: the last receive row stays up.
    /// </summary>
    public void TryProcess(ReadOnlySpan<short> samples, int sampleRateHz, bool transmitting)
    {
        if (transmitting || samples.Length < 2 || !IsDemanded) return;
        long now = _time.GetTimestamp();
        double intervalMs = MinFrameInterval.TotalMilliseconds;
        if (_framed)
        {
            var since = _time.GetElapsedTime(_lastFrame, now);
            if (since < MinFrameInterval) return;
            intervalMs = Math.Min(since.TotalMilliseconds, 1000.0);
        }
        _framed = true;
        _lastFrame = now;

        var viewport = _analyzer.Analyze(
            samples, sampleRateHz, _panWork, _wfWork,
            zoomLevel: 1, WidebandSpectrumAnalyzer.DisplayCenterHz, intervalMs);
        lock (_gate)
        {
            _panWork.AsSpan().CopyTo(_pan);
            _wfWork.AsSpan().CopyTo(_wf);
            _centerHz = viewport.CenterHz;
            _hzPerPixel = viewport.HzPerPixel;
            _seq++;
        }
    }

    /// <summary>
    /// The latest row as bytes, little-endian: int32 width, int64 sequence,
    /// int64 centre Hz, float32 Hz per pixel, then width float32 trace dB and
    /// width float32 waterfall dB. Null before the first row.
    /// </summary>
    public byte[]? Snapshot()
    {
        lock (_gate)
        {
            if (_seq == 0) return null;
            int w = _pan.Length;
            var bytes = new byte[4 + 8 + 8 + 4 + 8 * w];
            var s = bytes.AsSpan();
            BitConverter.TryWriteBytes(s, w);
            BitConverter.TryWriteBytes(s[4..], _seq);
            BitConverter.TryWriteBytes(s[12..], _centerHz);
            BitConverter.TryWriteBytes(s[20..], _hzPerPixel);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(_pan.AsSpan()).CopyTo(s[24..]);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(_wf.AsSpan()).CopyTo(s[(24 + 4 * w)..]);
            return bytes;
        }
    }
}
