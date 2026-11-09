// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>Maps core VFO and radio-tuning routes.</summary>
public static class RadioTuningEndpoints
{
    public static IEndpointRouteBuilder MapVfoEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapPost("/api/vfo", (VfoSetRequest req, RadioService r) =>
        {
            if (!r.IsExternalFrequencyAvailable(req.Hz))
            {
                log.LogInformation(
                    "api.vfo rejected receiver={Receiver} hz={Hz}",
                    req.Receiver,
                    req.Hz);
                return Results.BadRequest(new
                {
                    error = "hz is outside the native radio range and enabled transverter profiles",
                });
            }
            // Debug, not Information: this fires once per wheel/drag tune tick.
            // At Information it floods the log inside the synchronous request
            // path during a scroll, inflating each /api/vfo handler's duration
            // and worsening the ThreadPool pressure that starves frame/audio
            // delivery (see Program.RaiseThreadPoolFloor).
            log.LogDebug("api.vfo receiver={Receiver} hz={Hz}", req.Receiver, req.Hz);
            return Results.Ok(r.SetReceiverVfo(req.Receiver, req.Hz));
        });

        endpoints.MapPost("/api/vfo/swap", (RadioService r) =>
        {
            log.LogInformation("api.vfo.swap");
            return r.SwapVfos();
        });

        endpoints.MapPost("/api/rx2", (Rx2SetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx2 enabled={Enabled} vfoBHz={VfoBHz} audioMode={AudioMode} afGainDb={AfGainDb}",
                req.Enabled, req.VfoBHz, req.AudioMode, req.AfGainDb);
            return r.SetRx2(req);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapRadioTuningEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        endpoints.MapGet("/api/radio/transverter", (
            string? radioKey,
            string? layoutId,
            TransverterSettingsStore store,
            LayoutStore layouts,
            RadioService radio) =>
        {
            var (key, layout) = ResolveTransverterWorkspace(radioKey, layoutId, layouts, radio, create: false);
            return Results.Ok(store.GetForLayout(layouts, key, layout));
        });

        endpoints.MapPut("/api/radio/transverter", (
            TransverterSettingsSetRequest req,
            TransverterSettingsStore store,
            LayoutStore layouts,
            RadioService radio,
            TxService tx) =>
        {
            if (tx.IsMoxOn || tx.IsTunOn || tx.IsTwoToneOn)
                return Results.Conflict(new
                {
                    error = "transverter settings cannot change while the transmitter is active",
                });
            var settings = new TransverterSettingsDto(
                req.Enabled,
                req.IfFrequencyHz,
                req.RfFrequencyHz,
                req.Bands,
                req.Bands is null && req.Enabled ? 0 : req.ActiveBandId);
            if (!TransverterFrequencyConverter.TryValidate(settings, out var error))
                return Results.BadRequest(new { error });

            var (radioKey, layoutId) = ResolveTransverterWorkspace(req.RadioKey, req.LayoutId, layouts, radio, create: true);
            req = req with { RadioKey = radioKey, LayoutId = layoutId };

            // Suppress intermediate notifications so TCI never observes the new
            // workspace flag with the old IF/RF anchors (or vice versa).
            if (!layouts.SetTransverterSelection(
                    req.RadioKey,
                    req.LayoutId,
                    req.Enabled,
                    settings.ActiveBandId,
                    notifyChanged: false))
                return Results.BadRequest(new { error = "radio/layout workspace not found" });

            store.Set(settings, notifyChanged: false);
            store.NotifyChanged();
            var effective = store.GetForLayout(layouts, req.RadioKey, req.LayoutId);
            log.LogInformation(
                "api.radio.transverter radio={Radio} layout={Layout} enabled={Enabled} activeBand={ActiveBand} bands={BandCount} ifHz={IfHz} rfHz={RfHz}",
                req.RadioKey,
                req.LayoutId,
                settings.Enabled,
                settings.ActiveBandId,
                effective.Bands?.Count ?? 0,
                settings.IfFrequencyHz,
                settings.RfFrequencyHz);
            return Results.Ok(effective);
        });

        // VFO lock (Thetis chkVFOLock): blocks operator dial tuning; CAT/TCI
        // still tune. Pure software guard, no hardware effect.
        endpoints.MapPost("/api/radio/vfo-lock", (VfoLockSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.vfo-lock locked={Locked}", req.Locked);
            return Results.Ok(r.SetVfoLock(req.Locked));
        });

        // RIT (Receiver Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. RX1 only.
        endpoints.MapPost("/api/rx/rit", (RitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.rx.rit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetRit(req.Enabled, req.Hz));
        });

        // XIT (Transmit Incremental Tuning). Both fields optional; Hz clamped to
        // ±99999. Moves only the transmitted carrier.
        endpoints.MapPost("/api/tx/xit", (XitSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.tx.xit enabled={Enabled} hz={Hz}", req.Enabled, req.Hz);
            return Results.Ok(r.SetXit(req.Enabled, req.Hz));
        });

        // Diversity combiner (Thetis DiversityForm). Every field optional.
        // P2/ANAN-class only (needs two phase-synchronous ADCs).
        endpoints.MapPost("/api/rx/diversity", (DiversitySetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.rx.diversity enabled={Enabled} gain={Gain} phaseDeg={PhaseDeg} sourceRx={SourceRx} referenceRx={ReferenceRx} output={Output}",
                req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx, req.ReferenceRx, req.Output);
            return Results.Ok(r.SetDiversity(req.Enabled, req.Gain, req.PhaseDeg, req.SourceRx, req.ReferenceRx, req.Output));
        });

        // ATU tune request (Thetis ATUTune). Holds the Apollo/Alex auto-tune bit
        // (Protocol-1 C0=0x12 C2[4]) for DurationMs then auto-clears.
        // Bench-verification pending — see RadioService.RequestAtuTune.
        endpoints.MapPost("/api/radio/atu/tune", (AtuTuneRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.atu.tune durationMs={DurationMs}", req.DurationMs);
            return Results.Ok(r.RequestAtuTune(req.DurationMs));
        });

        endpoints.MapPost("/api/tx/vfo", (TxVfoSetRequest req, RadioService r) =>
        {
            if (!Enum.IsDefined(req.TxVfo))
                return Results.BadRequest(new { error = $"unknown txVfo {req.TxVfo}" });
            log.LogInformation("api.tx.vfo txVfo={TxVfo}", req.TxVfo);
            return Results.Ok(r.SetTxVfo(req.TxVfo));
        });

        // Select the transmit target by receiver index (0=RX1, 1=RX2, >=2 extra
        // DDC). Generalises /api/tx/vfo beyond the A/B pair; an out-of-range or
        // not-exposed index clamps to RX1 server-side.
        endpoints.MapPost("/api/tx/receiver", (TxReceiverSetRequest req, RadioService r) =>
        {
            log.LogInformation(
                "api.tx.receiver index={Index} requireIdle={RequireIdle}", req.Index, req.RequireIdle);
            var selected = SelectTxReceiver(req, r);
            if (selected.StatusCode == StatusCodes.Status400BadRequest)
                return Results.BadRequest(new { error = selected.Error });
            if (selected.StatusCode == StatusCodes.Status409Conflict)
                return Results.Conflict(new { error = selected.Error });
            return Results.Ok(selected.State);
        });

        endpoints.MapPost("/api/tx/split", (SplitSetRequest req, RadioService r) =>
        {
            var state = r.Snapshot();
            if (!SplitReceiverAvailable(state, req.Receiver))
                return Results.BadRequest(new { error = "split receiver is not exposed" });
            log.LogInformation("api.tx.split receiver={Receiver} enabled={Enabled}", req.Receiver, req.Enabled);
            return Results.Ok(r.SetSplit(req.Receiver, req.Enabled));
        });

        endpoints.MapPost("/api/tx/split/frequency", (SplitFrequencySetRequest req, RadioService r) =>
        {
            var state = r.Snapshot();
            if (!SplitReceiverAvailable(state, req.Receiver))
                return Results.BadRequest(new { error = "split receiver is not exposed" });
            if (!r.IsExternalFrequencyAvailable(req.Hz))
                return Results.BadRequest(new { error = "hz is outside the native radio range and enabled transverter profiles" });
            log.LogInformation("api.tx.split.frequency receiver={Receiver} hz={Hz}", req.Receiver, req.Hz);
            return Results.Ok(r.SetSplitFrequency(req.Receiver, req.Hz));
        });

        // Set the radio's hardware NCO (LO) frequency directly. Does not
        // move VfoHz — used by the panadapter pure-pan gesture when a drag
        // would carry the viewport outside the IQ capture window.
        // See docs/prd/panfall_behavior.md.
        endpoints.MapPost("/api/radio/lo", (RadioLoSetRequest req, RadioService r) =>
        {
            if (!r.IsExternalFrequencyAvailable(req.Hz, allowZero: true))
            {
                log.LogInformation("api.radio.lo rejected hz={Hz}", req.Hz);
                return Results.BadRequest(new { error = "hz is outside the native radio range and enabled transverter profiles" });
            }
            // Debug, not Information: a ruler/pure-pan drag posts this once per
            // frame — see the api.vfo note above.
            log.LogDebug("api.radio.lo hz={Hz}", req.Hz);
            return Results.Ok(r.SetRadioLo(req.Hz));
        });

        // SimpleHPSDR: where the dial sits across the display (-0.9..0.9, 0 = centre).
        endpoints.MapPost("/api/radio/dial-position", (DialPositionSetRequest req, RadioService r) =>
        {
            if (!double.IsFinite(req.Position))
                return Results.BadRequest(new { error = "position must be a number" });
            log.LogDebug("api.radio.dialPosition position={Position}", req.Position);
            return Results.Ok(r.SetDialPosition(req.Position));
        });

        return endpoints;
    }

    /// <summary>
    /// Map one transmitter select. <see cref="TxReceiverSetRequest.RequireIdle"/>
    /// false keeps <see cref="RadioService.SetTxReceiver(int)"/>. True refuses
    /// with 409 before the index changes.
    /// </summary>
    internal static TxReceiverSelection SelectTxReceiver(TxReceiverSetRequest req, RadioService radio)
    {
        if (req.Index < 0 || req.Index >= WireContract.MaxReceivers)
        {
            return new TxReceiverSelection(
                StatusCodes.Status400BadRequest,
                $"tx receiver index out of range (0..{WireContract.MaxReceivers - 1})",
                null);
        }
        if (!req.RequireIdle)
            return new TxReceiverSelection(StatusCodes.Status200OK, null, radio.SetTxReceiver(req.Index));
        if (!radio.TrySetTxReceiver(req.Index, requireIdle: true, out var state, out var error))
            return new TxReceiverSelection(StatusCodes.Status409Conflict, error, state);
        return new TxReceiverSelection(StatusCodes.Status200OK, null, state);
    }

    internal readonly record struct TxReceiverSelection(int StatusCode, string? Error, StateDto? State);

    private static bool SplitReceiverAvailable(StateDto state, int receiver) =>
        receiver == 0 || receiver > 0
            && receiver < WireContract.MaxReceivers
            && state.Receivers?.Any(item => item.Index == receiver && item.Enabled) == true;

    public static IEndpointRouteBuilder MapCtunEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Toggle CTUN (click-tune / centred tuning). When enabled, panadapter
        // clicks move only the dial and leave the hardware NCO frozen so the
        // operator can tune off-centre; TX retunes to the dial on key-down.
        // See docs/prd/panfall_behavior.md and StateDto.CtunEnabled.
        endpoints.MapPost("/api/radio/ctun", (CtunSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.ctun enabled={Enabled}", req.Enabled);
            return r.SetCtunEnabled(req.Enabled);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapFullDuplexMultiRxEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Toggle full-duplex RX audio ("DUP"). See
        // StateDto.FullDuplexMultiRxEnabled for what this gates.
        endpoints.MapPost("/api/radio/full-duplex-multi-rx", (FullDuplexMultiRxSetRequest req, RadioService r) =>
        {
            log.LogInformation("api.radio.fullDuplexMultiRx enabled={Enabled}", req.Enabled);
            return r.SetFullDuplexMultiRxEnabled(req.Enabled);
        });

        return endpoints;
    }

    // SimpleHPSDR fork: the transverter switch and active band live in a web-UI
    // workspace, and the engine only obeys the connected radio's active one. A
    // client without workspaces (SimpleHPSDR, scripts) sends the defaults, which
    // named a workspace that never exists, so every change failed with
    // "radio/layout workspace not found" and reads never showed the radio's state.
    // Defaults now mean the connected radio and its active workspace, and setting
    // creates an empty workspace when the radio has none. Named ones are unchanged.
    internal static (string RadioKey, string LayoutId) ResolveTransverterWorkspace(
        string? radioKey,
        string? layoutId,
        LayoutStore layouts,
        RadioService radio,
        bool create)
    {
        static bool IsDefault(string? s) =>
            string.IsNullOrWhiteSpace(s) || string.Equals(s.Trim(), "default", StringComparison.Ordinal);

        // The same key the engine reads the live state under (GetForConnectedRadio),
        // "Unknown" while no radio is connected.
        var key = IsDefault(radioKey) ? radio.ConnectedBoardKind.ToString() : radioKey!;
        var layout = layoutId ?? "default";
        if (!IsDefault(layout)) return (key, layout);

        var existing = layouts.GetForRadio(key);
        if (existing.Layouts.Any(l => l.Id == layout)) return (key, layout);
        if (existing.ActiveLayoutId is { Length: > 0 } active && existing.Layouts.Any(l => l.Id == active))
            return (key, active);
        if (existing.Layouts.Count > 0)
        {
            // Workspaces but none active: make the first one active, or the
            // change would land in a workspace the engine doesn't read.
            var first = existing.Layouts[0].Id;
            if (create) layouts.SetActive(key, first);
            return (key, first);
        }
        if (create) layouts.UpsertNamed(key, "default", "Default", "{}");
        return (key, "default");
    }
}
