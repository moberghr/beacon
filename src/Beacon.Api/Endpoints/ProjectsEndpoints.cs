using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Projects.Members;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class ProjectsEndpoints
{
    public static RouteGroupBuilder MapProjectsEndpoints(this RouteGroupBuilder group)
    {
        var projects = group.MapGroup("/projects").WithTags("Projects");

        projects.MapGet("/", ([AsParameters] GetProjectsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetProjects");

        projects.MapGet("/{id:int}", async Task<Results<Ok<GetProjectDetailResult>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetProjectDetailQuery(id), ct);
            return result.Project is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        }).WithName("GetProjectDetail");

        projects.MapPost("/", async (CreateProjectCommand cmd, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(cmd, ct);
            return TypedResults.Created($"/beacon/api/projects/{result.ProjectId}", result);
        }).WithName("CreateProject");

        // Setting, replacing or clearing a repository's access token is a SecurityAdmin's; CreateProject checks a token it
        // gets.
        projects.MapPut("/repositories/{id:int}/token", async (
            int id, UpdateRepositoryTokenRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateRepositoryTokenCommand(id, body.AccessToken), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateRepositoryToken").RequireBeaconPermission(BeaconPermissions.SecurityAdminPolicy);

        projects.MapGet("/{id:int}/documentation", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetProjectDocumentationQuery(id), ct))
            .WithName("GetProjectDocumentation");

        projects.MapGet("/{id:int}/imported-documents", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetImportedDocumentsQuery(id), ct))
            .WithName("GetImportedDocuments");

        projects.MapGet("/{id:int}/imported-documents/{documentId:int}", async Task<Results<Ok<GetImportedDocumentResult>, NotFound>> (
            int id, int documentId, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetImportedDocumentQuery(id, documentId), ct);
            return result.Document is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        })
            .WithName("GetImportedDocument");

        projects.MapPut("/documentation-sections/{id:int}", async (
            int id, UpdateDocumentationSectionRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateDocumentationSectionCommand(id, body.Content), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateDocumentationSection");

        projects.MapPost("/{id:int}/generate-documentation", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GenerateProjectDocumentationCommand(id, 0), ct))
            .WithName("GenerateProjectDocumentation")
            .WithBeaconLongRunning();

        projects.MapGet("/documentation/{id:int}/export", async (
            int id,
            [FromQuery(Name = "asHtml")] bool asHtml,
            IMediator m,
            CancellationToken ct) =>
        {
            var result = await m.Send(new ExportProjectDocumentationCommand(id, asHtml), ct);
            var bytes = System.Text.Encoding.UTF8.GetBytes(result.Content);
            return Results.File(bytes, result.ContentType, result.FileName);
        }).WithName("ExportProjectDocumentation");

        projects.MapPost("/documentation-sections/{id:int}/instruct", (
                int id, InstructDocumentationRequest body, IMediator m, CancellationToken ct) =>
                m.Send(new InstructDocumentationCommand(id, body.Instruction), ct))
            .WithName("InstructDocumentation")
            .WithBeaconLongRunning();

        projects.MapPost("/{id:int}/scan", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ScanAllRepositoriesCommand(id), ct))
            .WithName("ScanAllRepositories")
            .WithBeaconLongRunning();

        projects.MapGet("/{id:int}/mcp-context", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetProjectMcpContextQuery(id), ct))
            .WithName("GetProjectMcpContext");

        // Membership and links: a ProjectAdmin of the project or an Admin; 409 while the host manages membership. A UserAdmin
        // manages the members of any project (never their own membership), but links no data source.
        projects.MapPost("/{id:int}/data-sources", (int id, LinkProjectDataSourcesRequest body, IMediator m, CancellationToken ct) =>
                m.Send(new LinkProjectDataSourcesCommand(id, body.DataSourceIds), ct))
            .WithName("LinkProjectDataSources");

        // Unlinking is an Admin's (live roles are checked again by the handler).
        projects.MapDelete("/{id:int}/data-sources/{dataSourceId:int}", (int id, int dataSourceId, IMediator m, CancellationToken ct) =>
                m.Send(new UnlinkProjectDataSourceCommand(id, dataSourceId), ct))
            .WithName("UnlinkProjectDataSource")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        projects.MapGet("/{projectId:int}/members", ([AsParameters] GetProjectMembersQuery query, IMediator m, CancellationToken ct) =>
                m.Send(query, ct))
            .WithName("GetProjectMembers")
            .AdmitBeaconPermission(BeaconPermissions.UserAdmin);

        projects.MapGet("/{id:int}/member-candidates", (int id, [FromQuery] string? search, IMediator m, CancellationToken ct) =>
                m.Send(new SearchProjectMemberCandidatesQuery(id, search), ct))
            .WithName("SearchProjectMemberCandidates")
            .AdmitBeaconPermission(BeaconPermissions.UserAdmin);

        projects.MapPut("/{id:int}/members/{userId:int}", (int id, int userId, SetProjectMemberRequest body, IMediator m, CancellationToken ct) =>
                m.Send(new SetProjectMemberCommand(id, userId, body.Role), ct))
            .WithName("SetProjectMember")
            .AdmitBeaconPermission(BeaconPermissions.UserAdmin);

        projects.MapDelete("/{id:int}/members/{userId:int}", async (int id, int userId, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RemoveProjectMemberCommand(id, userId), ct);
            return TypedResults.NoContent();
        }).WithName("RemoveProjectMember")
            .AdmitBeaconPermission(BeaconPermissions.UserAdmin);

        // Restricting or opening a project is a UserAdmin's or an Admin's (live roles are checked again by the handler).
        projects.MapPut("/{id:int}/access", (int id, SetProjectAccessModeRequest body, IMediator m, CancellationToken ct) =>
                m.Send(new SetProjectAccessModeCommand(id, body.RequiresMembership), ct))
            .WithName("SetProjectAccessMode")
            .RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        return group;
    }
}

internal sealed record UpdateRepositoryTokenRequest(string? AccessToken);
internal sealed record UpdateDocumentationSectionRequest(string Content);
internal sealed record InstructDocumentationRequest(string Instruction);
internal sealed record LinkProjectDataSourcesRequest(List<int> DataSourceIds);
internal sealed record SetProjectMemberRequest(ProjectRole Role);
internal sealed record SetProjectAccessModeRequest(bool RequiresMembership);
