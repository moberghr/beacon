using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.ExecutionLog;
using MediatR;

namespace Beacon.Api.Endpoints;

internal static class ExecutionLogEndpoints
{
    public static RouteGroupBuilder MapExecutionLogEndpoints(this RouteGroupBuilder group)
    {
        // The execution log of the statements Beacon runs on data sources is an Auditor's or a SecurityAdmin's: a paged read
        // and a CSV export, each over at most 93 days and only the data sources the caller may read. Both are recorded in the
        // security audit trail.
        var log = group.MapGroup("/execution-log").WithTags("ExecutionLog");

        log.MapGet("/", ([AsParameters] GetExecutionLogQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetExecutionLog")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        log.MapGet("/export", async ([AsParameters] ExportExecutionLogQuery query, IMediator m, HttpContext http, CancellationToken ct) =>
            {
                var export = await m.Send(query, ct);
                http.Response.Headers["X-Beacon-Rows"] = export.Rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
                http.Response.Headers["X-Beacon-Truncated"] = export.Truncated ? "true" : "false";

                return Results.File(export.Content, "text/csv", export.FileName);
            })
            .WithName("ExportExecutionLog")
            .RequireBeaconPermission(BeaconPermissions.AuditorOrSecurityAdminPolicy);

        return group;
    }
}
