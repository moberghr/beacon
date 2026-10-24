using Beacon.Core.Authorization;
using Beacon.Core.Services.Embed;
using MediatR;

namespace Beacon.Core.Handlers.Embed;

/// <summary>
/// What the embed page needs to know about a grant before it is served: whether the grant is live
/// (<see cref="EmbedGrantResolver"/>) and the frame origins it may be shown in. Nothing else: the page renders through the
/// token, never through this.
/// </summary>
internal sealed class GetEmbedFrameHandler(EmbedGrantResolver grants) : IRequestHandler<GetEmbedFrameQuery, GetEmbedFrameResult>
{
    public async Task<GetEmbedFrameResult> Handle(GetEmbedFrameQuery request, CancellationToken cancellationToken)
    {
        var resolution = await grants.ResolvePublicIdAsync(request.PublicId, cancellationToken);

        return resolution.IsLive
            ? new GetEmbedFrameResult(true, resolution.Grant!.AllowedOrigins)
            : new GetEmbedFrameResult(false, []);
    }
}

/// <summary>The embed page of the grant named by <see cref="PublicId"/>.</summary>
[ProjectScopeExempt("the anonymous embed page: it reads only whether the grant is live and its frame origins, and renders nothing")]
public sealed record GetEmbedFrameQuery(string PublicId) : IRequest<GetEmbedFrameResult>;

/// <param name="Available">The grant is live: not revoked, not expired, its version not superseded.</param>
/// <param name="AllowedOrigins">The origins that may frame the page; empty when it is not available.</param>
public sealed record GetEmbedFrameResult(bool Available, IReadOnlyList<string> AllowedOrigins);
