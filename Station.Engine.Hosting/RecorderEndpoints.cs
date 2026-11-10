// SPDX-License-Identifier: GPL-2.0-or-later
//
// SimpleHPSDR: the recorder (AudioRecorderService). Files are plain names
// inside the recordings folder (GET /api/recorder gives it); the app lists,
// renames and deletes them itself.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SimpleHPSDR.Server;

public static class RecorderEndpoints
{
    public static IEndpointRouteBuilder MapRecorderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/recorder", (AudioRecorderService rec) => Results.Ok(rec.Status()));
        endpoints.MapPost("/api/recorder/record", (RecorderRecordRequest req, AudioRecorderService rec) =>
            rec.StartRecording(req, out var error) ? Results.Ok(rec.Status()) : Results.Conflict(new { error }));
        endpoints.MapPost("/api/recorder/record/stop", (AudioRecorderService rec) =>
        {
            rec.StopRecording();
            return Results.Ok(rec.Status());
        });
        endpoints.MapPost("/api/recorder/play", (RecorderPlayRequest req, AudioRecorderService rec) =>
            rec.StartPlayback(req, out var error) ? Results.Ok(rec.Status()) : Results.Conflict(new { error }));
        endpoints.MapPost("/api/recorder/play/stop", (AudioRecorderService rec) =>
        {
            rec.StopPlayback();
            return Results.Ok(rec.Status());
        });
        return endpoints;
    }
}
