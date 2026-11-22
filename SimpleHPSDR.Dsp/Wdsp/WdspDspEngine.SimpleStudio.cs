// SPDX-License-Identifier: GPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 KC2RGW Chris
//
// SimpleStudio on the TX mic block: the operator's chain of effects, first at
// the seam (after the TX-audio plugins, before DEXP, SimpleExpander and
// fexchange2 into TXA). See SimpleHPSDR.Dsp/Studio/SimpleStudio.cs. Digital,
// tone and recorded-audio transmissions bypass it, like the expanders.

using Microsoft.Extensions.Logging;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Dsp.Studio;

namespace SimpleHPSDR.Dsp.Wdsp;

public sealed partial class WdspDspEngine
{
    private readonly SimpleStudio _simpleStudio = new();

    public void SetSimpleStudioConfig(SimpleStudioConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (_disposed != 0) return;
        _simpleStudio.SetRate(_txaInputRateHz);
        _simpleStudio.Configure(cfg);
        var c = _simpleStudio.Config;
        _log.LogInformation(
            "wdsp.setSimpleStudio enabled={Enabled} effects={Count} latency={Latency} samples",
            c.Enabled, c.Effects?.Count ?? 0, _simpleStudio.Latency);
    }

    public SimpleStudioMetersDto GetSimpleStudioMeters() =>
        _disposed != 0 ? SimpleStudioMetersDto.Inactive : _simpleStudio.ReadMeters();

    // Realtime path.
    private void ProcessSimpleStudioBlock(Span<float> mic, bool bypass)
    {
        if (bypass || !_simpleStudio.Wanted)
        {
            _simpleStudio.NoteSkipped();
            return;
        }
        _simpleStudio.Process(mic, _txaInputRateHz);
    }
}
