// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Net;
using System.Text.Json;
using SimpleHPSDR.Contracts;
using SimpleHPSDR.Dsp.Wdsp;

namespace SimpleHPSDR.Server;

/// <summary>Maps the versioned SPA-to-station protocol discovery surface.</summary>
public static class StationProtocolEndpoints
{
    public const int CurrentProtocolVersion = 1;

    public static IEndpointRouteBuilder MapStationProtocolEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // Additive. Old engines omit both fields and decode FreeDV on RX1 only.
        // maxReceivers is the shared wire ceiling, not the connected board count.
        endpoints.MapGet("/api/station/version", () => Results.Ok(new
        {
            protocol = CurrentProtocolVersion,
            engine = EngineVersion,
            freeDvReceiverSelection = true,
            maxReceivers = WireContract.MaxReceivers,
        }));

        // The closed product bundle owns hardware-diagnostics interpretation.
        // This is its sole diagnostics read seam into the published engine:
        // return the snapshot DspPipelineService already computes, unchanged.
        endpoints.MapGet("/api/station/dsp-diagnostics", (HttpContext context) =>
        {
            var services = context.RequestServices;
            var dsp = services.GetRequiredService<DspPipelineService>();
            var wisdom = services.GetRequiredService<WdspWisdomInitializer>();
            return Results.Ok(dsp.SnapshotDiagnostics(wisdom));
        });

        endpoints.MapGet("/api/station/rade/status", (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            return port is null ? Results.NotFound() : Results.Ok(port.RadeStatus());
        });

        endpoints.MapPost("/api/station/rade/select", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<RadeSelectRequest>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "RADE selection is required" });
            return port.SelectRade(request.Active, request.TxText)
                ? Results.Ok(port.RadeStatus())
                : Results.Conflict(new { error = "RADE is unavailable for this station-engine RID/build" });
        });

        // SimpleHPSDR: RADE's squelch, mic gate and TX level.
        endpoints.MapGet("/api/station/rade/tuning", (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            return port is null ? Results.NotFound() : Results.Ok(port.RadeTuning());
        });

        endpoints.MapPost("/api/station/rade/tuning", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<RadeTuning>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "RADE tuning is required" });
            return Results.Ok(port.SetRadeTuning(request));
        });

        endpoints.MapPost("/api/station/rade/mic", async (HttpContext context) =>
        {
            if (!IsLoopback(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var port = context.RequestServices.GetService<CompositeAudioModemPort>();
            if (port is null) return Results.NotFound();
            var request = await ReadRequestAsync<RadeMicConfig>(context).ConfigureAwait(false);
            if (request is null) return Results.BadRequest(new { error = "RADE mic config is required" });
            port.SetRadeMicConfig(request);
            return Results.Ok(port.RadeStatus());
        });

        return endpoints;
    }

    internal static string EngineVersion =>
        typeof(StreamingHub).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-unknown";

    private static bool IsLoopback(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private static async Task<T?> ReadRequestAsync<T>(HttpContext context) where T : class
    {
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
