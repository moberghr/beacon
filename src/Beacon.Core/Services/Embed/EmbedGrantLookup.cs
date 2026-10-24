using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// How a grant is found: by its public id (what a token's <c>sub</c> and an embed URL name) or by its secret's hash (what a
/// token exchange presents). Revoked and expired grants are found too, so the caller can say why it refuses one.
/// </summary>
internal static class EmbedGrantLookup
{
    public static IQueryable<EmbedGrant> ByPublicId(BeaconContext context, string publicId)
    {
        return context.EmbedGrants
            .Where(x => x.PublicId == publicId);
    }

    public static IQueryable<EmbedGrant> BySecretHash(BeaconContext context, string secretHash)
    {
        return context.EmbedGrants
            .Where(x => x.SecretHash == secretHash);
    }
}
