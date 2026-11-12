// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Runtime.InteropServices;

namespace SimpleHPSDR.Server;

/// <summary>Maps the standalone engine's browser-bootstrap capabilities.</summary>
public static class StationEngineCapabilitiesEndpoints
{
    public static IEndpointRouteBuilder MapStationEngineCapabilitiesEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/capabilities", (StationEngineCapabilitiesService caps) =>
            Results.Ok(caps.Snapshot));
        // Orderly stop for the owning app: lets the host dispose its services,
        // so RadioService writes its last state instead of losing up to a
        // second of changes to a kill. Loopback callers only.
        endpoints.MapPost("/api/engine/shutdown", (HttpContext http, IHostApplicationLifetime lifetime) =>
        {
            if (http.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            lifetime.StopApplication();
            return Results.Accepted();
        });
        return endpoints;
    }
}

internal sealed class StationEngineCapabilitiesService
{
    public StationEngineCapabilitiesService(
        IConfiguration configuration,
        IReadOnlyList<string>? lanHttpsUrls = null)
    {
        var advertisedLanHttpsUrls = lanHttpsUrls?.ToArray() ?? Array.Empty<string>();
        Snapshot = new StationEngineCapabilitiesSnapshot(
            Host: "server",
            Platform: DetectPlatform(),
            Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            Version: StationProtocolEndpoints.EngineVersion,
            LanHttpsUrls: advertisedLanHttpsUrls,
            DisplayPerformance: DisplayPerformanceOptions.Resolve(configuration),
            Features: new StationEngineFeatureMatrix(
                LanBrowser: advertisedLanHttpsUrls.Length > 0));
    }

    public StationEngineCapabilitiesSnapshot Snapshot { get; }

    private static string DetectPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "unknown";
    }
}

internal sealed record StationEngineCapabilitiesSnapshot(
    string Host,
    string Platform,
    string Architecture,
    string Version,
    IReadOnlyList<string> LanHttpsUrls,
    DisplayPerformanceSnapshot DisplayPerformance,
    StationEngineFeatureMatrix Features);

// Per-family feature availability for the trimmed standalone engine. The
// engine hosts only the radio/DSP/TX surface — none of the product-side route
// families below — so every product flag defaults to false. The full
// SimpleHPSDR.Server host reports the same families as true (see SimpleHPSDR.Server.Hosting
// FeatureMatrix), which lets the SPA distinguish "trimmed engine" from "full
// server" instead of probing routes into 404s. Field names must stay aligned
// with the FeatureFamily keys parsed in simplehpsdr-web/src/api/capabilities.ts.
//
// UiPersistence is the deliberate exception: the trimmed engine DOES serve the
// operator-UI persistence surface (/api/ui/* and /api/pan-wf-split — see
// OperatorUiSettingsEndpoints / PanWfSplitStore), so it reports true. A gate
// that trusted a false value here would wrongly disable working layout /
// panadapter-split persistence in attach mode.
internal sealed record StationEngineFeatureMatrix(
    bool Chat = false,
    bool Logbook = false,
    bool Spots = false,
    bool Rotator = false,
    bool Hamclock = false,
    bool Digimodes = false,
    bool Midi = false,
    bool Admin = false,
    bool Remote = false,
    bool System = false,
    bool Diagnostics = false,
    bool UiPersistence = true,
    bool FrontPanel = false,
    bool LanBrowser = false,
    bool GodsEye = false,
    bool Plugins = false);
