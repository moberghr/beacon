using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Testing;

/// <summary>
/// Delegated access tokens shaped like the ones Microsoft Entra issues to an MCP client (Claude's connector) — <c>tid</c>,
/// <c>oid</c>, <c>scp</c>, <c>azp</c>, the tenant's v2 issuer and the registration's audience — signed with a key made for
/// the test run. A gateway accepts them only after <see cref="BeaconTestingServiceCollectionExtensions.TrustMcpTestTokens"/>,
/// which only a test host calls.
/// </summary>
public sealed class McpTestTokens
{
    /// <summary>The client application the tokens are issued to unless a test names another.</summary>
    public const string DefaultClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    /// <param name="tenantId">The tenant the gateway accepts (<c>AzureAd:TenantId</c>).</param>
    /// <param name="audience">The registration the gateway accepts (<c>AzureAd:ClientId</c>).</param>
    public McpTestTokens(string tenantId, string audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        TenantId = tenantId;
        Audience = audience;
    }

    public string TenantId { get; }

    public string Audience { get; }

    /// <summary>The issuer the tokens carry: the tenant's Entra v2 issuer, as the gateway validates it.</summary>
    public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>A symmetric key generated for this instance; never a fixed value.</summary>
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>
    /// A delegated token for the person <paramref name="objectId"/> carrying <paramref name="scope"/>, issued to
    /// <paramref name="clientId"/>, valid for ten minutes.
    /// </summary>
    public string For(string objectId, string scope = "mcp.access", string clientId = DefaultClientId, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        List<Claim> claims =
        [
            new("tid", TenantId),
            new("oid", objectId),
            new("sub", $"pairwise-{objectId}"),
            new("scp", scope),
            new("azp", clientId)
        ];
        if (name != null)
        {
            claims.Add(new Claim("name", name));
        }

        return Write(claims);
    }

    /// <summary>A token with exactly <paramref name="claims"/> (besides issuer, audience and lifetime): for refusal cases.</summary>
    public string With(IEnumerable<Claim> claims) => Write([.. claims]);

    private string Write(List<Claim> claims)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Audience, claims, now.AddMinutes(-1), now.AddMinutes(10), credentials));
    }
}
