using Beacon.Core.Handlers.Projects;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class ProjectsEndpoints
{
    public static RouteGroupBuilder MapProjectsEndpoints(this RouteGroupBuilder group)
    {
        var projects = group.MapGroup("/projects").WithTags("Projects");

        projects.MapGet("/", (IMediator m, CancellationToken ct) => m.Send(new GetProjectsQuery(), ct))
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

        projects.MapPut("/repositories/{id:int}/token", async (
            int id, UpdateRepositoryTokenRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateRepositoryTokenCommand(id, body.AccessToken), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateRepositoryToken");

        projects.MapGet("/{id:int}/documentation", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetProjectDocumentationQuery(id), ct))
            .WithName("GetProjectDocumentation");

        projects.MapPut("/documentation-sections/{id:int}", async (
            int id, UpdateDocumentationSectionRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateDocumentationSectionCommand(id, body.Content), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateDocumentationSection");

        projects.MapPost("/{id:int}/generate-documentation", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GenerateProjectDocumentationCommand(id, 0), ct))
            .WithName("GenerateProjectDocumentation");

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
            .WithName("InstructDocumentation");

        projects.MapPost("/{id:int}/scan", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ScanAllRepositoriesCommand(id), ct))
            .WithName("ScanAllRepositories");

        projects.MapGet("/{id:int}/mcp-context", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetProjectMcpContextQuery(id), ct))
            .WithName("GetProjectMcpContext");

        return group;
    }
}

internal sealed record UpdateRepositoryTokenRequest(string? AccessToken);
internal sealed record UpdateDocumentationSectionRequest(string Content);
internal sealed record InstructDocumentationRequest(string Instruction);
