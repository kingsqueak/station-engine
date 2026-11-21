// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleExpander on the TX mic block: the managed alternative to DEXP, at the
// same seam (after the TX-audio plugins, before fexchange2 into TXA). See
// SimpleHPSDR.Dsp/SimpleExpander.cs for the processing. Digital, tone and
// recorded-audio transmissions bypass it, like DEXP.

using Microsoft.Extensions.Logging;
using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Dsp.Wdsp;

public sealed partial class WdspDspEngine
{
    private readonly SimpleExpander _simpleExpander = new();
    // Audio thread only: a stretch without processing clears the state first.
    private bool _simpleExpanderNeedsReset = true;

    public void SetSimpleExpanderConfig(SimpleExpanderConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (_disposed != 0) return;
        _simpleExpander.Configure(cfg);
        var c = _simpleExpander.Config;
        _log.LogInformation(
            "wdsp.setSimpleExpander enabled={Enabled} thresh={Thresh:F1}dB ratio={Ratio:F1} range={Range:F1}dB attack={Attack:F1}ms hold={Hold:F0}ms release={Release:F0}ms",
            c.Enabled, c.ThresholdDb, c.Ratio, c.RangeDb, c.AttackMs, c.HoldMs, c.ReleaseMs);
    }

    public SimpleExpanderMeterDto GetSimpleExpanderMeter() =>
        _disposed != 0 ? SimpleExpanderMeterDto.Inactive : _simpleExpander.ReadMeter();

    // Realtime path. The detector sees the mic at the level it will have after
    // the TXA panel's mic gain, so the threshold matches the mic meter.
    private void ProcessSimpleExpanderBlock(Span<float> mic, bool bypass)
    {
        if (bypass || !_simpleExpander.Wanted)
        {
            _simpleExpanderNeedsReset = true;
            return;
        }
        if (_simpleExpanderNeedsReset)
        {
            _simpleExpander.Reset();
            _simpleExpanderNeedsReset = false;
        }
        _simpleExpander.Process(mic, _txaInputRateHz, _txPanelGain);
    }
}
