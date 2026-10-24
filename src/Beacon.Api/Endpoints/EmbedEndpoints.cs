using Beacon.Api.Authentication;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Services.Embed;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

/// <summary>
/// The anonymous embed surface, mapped outside the <c>/beacon/api</c> group and only while embedding is on: the token
/// exchange (a host's backend, with its grant secret), the render (Beacon's embed page, with a token) and the embed page
/// itself. No cookie, antiforgery or <c>BeaconApi</c> policy applies; the embed middleware decides the caller
/// (<see cref="EmbedTokenAuthMiddleware"/>) and <see cref="EmbedEndpointFilter"/> admits only its principal. The render takes
/// nothing from the request body: the grant, its project and the parameter values are the verified token's.
/// </summary>
internal static class EmbedEndpoints
{
    /// <summary>The embed page within the web root: Beacon.UI's static web assets, under their <c>/beacon</c> base path.</summary>
    public const string PageFile = "beacon/embed.html";

    /// <summary>The meta element the served page carries its frame marker in.</summary>
    public const string FrameMarkerMeta = "beacon-embed-frame";

    public static IEndpointRouteBuilder MapEmbedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(BeaconEmbedPaths.TokensPath, async ([FromBody] IssueEmbedTokenBody? body, HttpContext context, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new IssueEmbedTokenCommand(body?.Parameters, body?.TtlSeconds), ct);

                return Results.Ok(result with { EmbedPath = context.Request.PathBase + result.EmbedPath });
            })
            .WithName("IssueEmbedToken")
            .WithTags("Embed")
            .AllowAnonymous()
            .AddEndpointFilter(new EmbedEndpointFilter(EmbedCredential.GrantSecret));

        endpoints.MapPost(BeaconEmbedPaths.RenderPath, async (HttpContext context, IMediator m, CancellationToken ct) =>
            {
                var embed = context.Features.Get<EmbedRequestFeature?>()!;

                return Results.Ok(await m.Send(new RenderEmbedQueryCommand(embed.ProjectId, embed.GrantId, embed.Parameters), ct));
            })
            .WithName("RenderEmbedQuery")
            .WithTags("Embed")
            .AllowAnonymous()
            .AddEndpointFilter(new EmbedEndpointFilter(EmbedCredential.Token));

        endpoints.MapGet(BeaconEmbedPaths.FramePrefix + "/{publicId}", GetFrameAsync)
            .WithName("GetEmbedFrame")
            .WithTags("Embed")
            .AllowAnonymous();

        return endpoints;
    }

    // The page, with the grant's frame headers and its frame marker; a grant that is not live is 404 that no site may frame.
    private static async Task GetFrameAsync(string publicId, HttpContext context, IMediator m, CancellationToken ct)
    {
        var limiter = context.RequestServices.GetRequiredService<EmbedRateLimiter>();
        var logger = context.RequestServices.GetRequiredService<ILogger<EmbedRateLimiter>>();
        EmbedFrameHeaders.Apply(context.Response, []);

        GetEmbedFrameResult frame;
        try
        {
            frame = await m.Send(new GetEmbedFrameQuery(publicId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The embed page could not be served: the grant store could not be read");
            await EmbedTokenAuthMiddleware.UnavailableAsync(context);
            return;
        }

        if (!frame.Available)
        {
            var status = limiter.TryAcquireRefusal(context, out var retryAfter)
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status429TooManyRequests;
            if (status == StatusCodes.Status429TooManyRequests)
            {
                context.Response.Headers.RetryAfter = EmbedRateLimiter.RetryAfterSeconds(retryAfter);
            }

            await EmbedTokenAuthMiddleware.WriteUnavailableAsync(context, status);
            return;
        }

        if (!limiter.TryAcquireGrant(exchange: false, "page:" + publicId, out var grantRetryAfter))
        {
            context.Response.Headers.RetryAfter = EmbedRateLimiter.RetryAfterSeconds(grantRetryAfter);
            await EmbedTokenAuthMiddleware.WriteUnavailableAsync(context, StatusCodes.Status429TooManyRequests);
            return;
        }

        var page = await ReadPageAsync(context, ct);
        var headEnd = page?.IndexOf("</head>", StringComparison.OrdinalIgnoreCase) ?? -1;
        if (page == null || headEnd < 0)
        {
            logger.LogWarning("The embed page is not installed: {PageFile} is missing from the web root or has no head", PageFile);
            await EmbedTokenAuthMiddleware.WriteUnavailableAsync(context, StatusCodes.Status404NotFound);
            return;
        }

        var marker = context.RequestServices.GetRequiredService<EmbedTokenCodec>().FrameMarkerOf(publicId);
        var html = page.Insert(headEnd, $"<meta name=\"{FrameMarkerMeta}\" content=\"{marker}\">");

        EmbedFrameHeaders.Apply(context.Response, frame.AllowedOrigins);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentLength = System.Text.Encoding.UTF8.GetByteCount(html);
        await context.Response.WriteAsync(html, ct);
    }

    private static async Task<string?> ReadPageAsync(HttpContext context, CancellationToken ct)
    {
        var file = context.RequestServices.GetService<IWebHostEnvironment>()?.WebRootFileProvider.GetFileInfo(PageFile);
        if (file is not { Exists: true, IsDirectory: false })
        {
            return null;
        }

        await using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);

        return await reader.ReadToEndAsync(ct);
    }
}

/// <param name="Parameters">A value for each of the grant's locked parameters, as text.</param>
/// <param name="TtlSeconds">A shorter lifetime than the default, in seconds; never longer than <c>MaxTokenTtlSeconds</c>.</param>
internal sealed record IssueEmbedTokenBody(IReadOnlyDictionary<string, string>? Parameters, int? TtlSeconds);
