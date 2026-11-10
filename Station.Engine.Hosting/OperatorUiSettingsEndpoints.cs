// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA), Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using SimpleHPSDR.Contracts;

namespace SimpleHPSDR.Server;

/// <summary>
/// Maps the operator UI preference routes (theme, display, toolbar, NR
/// disclosure, clock tile, operator identity, bottom-row pins, pan/waterfall split) on the
/// standalone station engine. These families were previously product-host-only,
/// so an attached SPA lost its saved theme / dB ranges / toolbar favorites /
/// operator profile on every launch — localStorage is only a fast-paint cache
/// and the loopback origin changes per launch. The handlers mirror the product
/// host's routes one-for-one so both hosts serve the same contract.
/// </summary>
public static class OperatorUiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapOperatorUiSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var log = endpoints.ServiceProvider.GetRequiredService<ILogger<object>>();

        // Panadapter background + dB windows + display performance. The PUT
        // pushes the validated, merged config into the running DSP pipeline so
        // the change is live without a reconnect — same as the product host.
        endpoints.MapGet("/api/display-settings", (DisplaySettingsStore store, DspPipelineService dsp) =>
            Results.Ok(store.Get() with { ActivePanadapterWidth = dsp.ConfiguredPanadapterWidth }));

        endpoints.MapPut("/api/display-settings", (DisplaySettingsSetRequest req, DisplaySettingsStore store, DspPipelineService dsp) =>
        {
            if (req.Mode is not null && string.IsNullOrWhiteSpace(req.Mode) ||
                req.Fit is not null && string.IsNullOrWhiteSpace(req.Fit))
                return Results.BadRequest(new { error = "mode and fit must not be blank when provided" });
            try
            {
                store.SaveMode(req.Mode, req.Fit, req.RxTraceColor,
                    req.DbMin, req.DbMax, req.TxDbMin, req.TxDbMax,
                    req.WfDbMin, req.WfDbMax, req.WfTxDbMin, req.WfTxDbMax,
                    req.TxDisplayCalOffsetDb, req.TxDisplayFftSize,
                    req.TxDisplayWindow, req.TxDisplayAvgTauMs,
                    req.WidebandDisplayEnabled,
                    req.DisplayMaxFrameRateHz,
                    req.DisplayDecimation,
                    req.WaterfallUpdatePeriod,
                    req.WidebandSignalMarkersEnabled,
                    req.WaterfallColormap,
                    req.WaterfallScrollSpeed,
                    req.BandOverlayEnabled,
                    req.BandEdgeAlertEnabled,
                    req.ChatRosterOverlayEnabled,
                    req.SpotLabelFontPx,
                    req.GlobeRenderer,
                    req.GlobeCustomImageryJson,
                    req.FilterPanelBgMode,
                    req.FilterPanelBgColor,
                    req.FilterPanelBgBrightness,
                    req.RxDisplayFftSize,
                    req.WaterfallLowColor,
                    req.WaterfallMidColor,
                    req.WaterfallHighColor,
                    req.BandwidthFilter,
                    req.CwTuningSpanHz,
                    req.CwSidebandByBand,
                    req.PanadapterRowPoints);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            var saved = store.Get();
            dsp.ApplyDisplaySettings(saved);
            return Results.Ok(saved with { ActivePanadapterWidth = dsp.ConfiguredPanadapterWidth });
        });

        // SimpleHPSDR fork: the SimpleHPSDR browser app's screen settings (background
        // images, bottom pin, panadapter/waterfall split, toolbar, NR screen
        // prefs, clock, theme) and the wideband signal markers are gone.

        // Shared operator identity (callsign + Maidenhead grid). The engine has
        // no QRZ session, so unlike the product host there is no QRZ home-
        // station fallback: the resolved values equal the saved override and
        // the FromQrz flags are always false. Same wire shape either way, so
        // the SPA's Settings page renders identically against both hosts.
        endpoints.MapGet("/api/operator",
            (OperatorIdentityStore store) => Results.Ok(Status(store)));

        endpoints.MapPost("/api/operator",
            (OperatorIdentity body, OperatorIdentityStore store) =>
            {
                store.Set(body);
                log.LogInformation("api.operator.set call={Call} grid={Grid}",
                    body.Callsign, body.Grid);
                return Results.Ok(Status(store));
            });

        return endpoints;
    }

    /// <summary>
    /// /api/operator status without a QRZ fallback source: the effective
    /// identity is exactly the saved override.
    /// </summary>
    internal static OperatorIdentityStatus Status(OperatorIdentityStore store)
    {
        var saved = store.Get();
        return new OperatorIdentityStatus(
            Callsign: saved.Callsign,
            Grid: saved.Grid,
            ResolvedCallsign: saved.Callsign,
            ResolvedGrid: saved.Grid,
            CallsignFromQrz: false,
            GridFromQrz: false);
    }
}
