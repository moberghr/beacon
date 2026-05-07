using Beacon.Core.Handlers.Projects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class ProjectsEndpoints
{
    public static RouteGroupBuilder MapProjectsEndpoints(this RouteGroupBuilder group)
    {
        var projects = group.MapGroup("/projects").WithTags("Projects");

        projects.MapGet("/", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetProjectsQuery(), ct)))
            .WithName("GetProjects")
            .Produces<GetProjectsResult>(StatusCodes.Status200OK);

        projects.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetProjectDetailQuery(id), ct);
                return result.Project is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetProjectDetail")
            .Produces<GetProjectDetailResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        projects.MapPost("/", async (CreateProjectCommand command, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(command, ct);
                return Results.Created($"/beacon/api/projects/{result.ProjectId}", result);
            })
            .WithName("CreateProject")
            .Produces<CreateProjectResult>(StatusCodes.Status201Created);

        projects.MapPut("/repositories/{id:int}/token", async (
                int id,
                UpdateRepositoryTokenRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new UpdateRepositoryTokenCommand(id, body.AccessToken), ct);
                return Results.NoContent();
            })
            .WithName("UpdateRepositoryToken")
            .Produces(StatusCodes.Status204NoContent);

        projects.MapGet("/{id:int}/documentation", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetProjectDocumentationQuery(id), ct)))
            .WithName("GetProjectDocumentation")
            .Produces<GetProjectDocumentationResult>(StatusCodes.Status200OK);

        projects.MapPut("/documentation-sections/{id:int}", async (
                int id,
                UpdateDocumentationSectionRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new UpdateDocumentationSectionCommand(id, body.Content), ct);
                return Results.NoContent();
            })
            .WithName("UpdateDocumentationSection")
            .Produces(StatusCodes.Status204NoContent);

        projects.MapPost("/{id:int}/generate-documentation", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GenerateProjectDocumentationCommand(id, 0), ct)))
            .WithName("GenerateProjectDocumentation")
            .Produces<GenerateProjectDocumentationResult>(StatusCodes.Status200OK);

        projects.MapGet("/documentation/{id:int}/export", async (
                int id,
                [FromQuery(Name = "asHtml")] bool asHtml,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new ExportProjectDocumentationCommand(id, asHtml), ct);
                var bytes = System.Text.Encoding.UTF8.GetBytes(result.Content);
                return Results.File(bytes, result.ContentType, result.FileName);
            })
            .WithName("ExportProjectDocumentation")
            .Produces(StatusCodes.Status200OK);

        projects.MapPost("/documentation-sections/{id:int}/instruct", async (
                int id,
                InstructDocumentationRequest body,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new InstructDocumentationCommand(id, body.Instruction), ct)))
            .WithName("InstructDocumentation")
            .Produces<InstructDocumentationResult>(StatusCodes.Status200OK);

        projects.MapPost("/{id:int}/scan", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ScanAllRepositoriesCommand(id), ct)))
            .WithName("ScanAllRepositories")
            .Produces<ScanAllRepositoriesResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record UpdateRepositoryTokenRequest(string? AccessToken);
internal sealed record UpdateDocumentationSectionRequest(string Content);
internal sealed record InstructDocumentationRequest(string Instruction);
