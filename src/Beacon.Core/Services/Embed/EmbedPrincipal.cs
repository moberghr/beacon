using System.Globalization;
using System.Security.Claims;
using Beacon.Core.Mcp;

namespace Beacon.Core.Services.Embed;

/// <summary>Which embed credential a request presented.</summary>
internal enum EmbedCredential
{
    /// <summary>A grant's secret, presented by the host's backend to exchange it for a token.</summary>
    GrantSecret,

    /// <summary>An embed token, presented by Beacon's embed page to render the grant's query.</summary>
    Token
}

/// <summary>
/// The principal Beacon's embed middleware puts on an embed request once its credential is verified: the grant's id and
/// how it was presented, and nothing else. It carries no <c>NameIdentifier</c>, name or role claim, so nothing that reads
/// those ever takes it for a user or a session; <see cref="Authorization.BeaconActorAccessor"/> recognises it first and
/// reads the grant (and its creator, for whom it acts) from the store.
/// </summary>
internal static class EmbedPrincipal
{
    public static ClaimsPrincipal Create(int grantId, EmbedCredential credential)
    {
        var claims = new[]
        {
            new Claim(McpCallerClaimTypes.AuthMethod, AuthMethodOf(credential)),
            new Claim(McpCallerClaimTypes.EmbedGrantId, grantId.ToString(CultureInfo.InvariantCulture))
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.EmbedAuthenticationType));
    }

    /// <summary>The grant and credential of an embed principal, or false for any other principal.</summary>
    public static bool TryRead(ClaimsPrincipal? principal, out int grantId, out EmbedCredential credential)
    {
        grantId = 0;
        credential = EmbedCredential.Token;
        if (principal?.Identity is not { IsAuthenticated: true, AuthenticationType: McpCallerClaimTypes.EmbedAuthenticationType })
        {
            return false;
        }

        var methods = principal.FindAll(McpCallerClaimTypes.AuthMethod)
            .Select(x => x.Value)
            .ToList();
        if (methods.Count != 1)
        {
            return false;
        }

        if (methods[0] == McpCallerClaimTypes.EmbedGrantAuthMethod)
        {
            credential = EmbedCredential.GrantSecret;
        }
        else if (methods[0] != McpCallerClaimTypes.EmbedTokenAuthMethod)
        {
            return false;
        }

        return int.TryParse(principal.FindFirst(McpCallerClaimTypes.EmbedGrantId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out grantId)
            && grantId > 0;
    }

    /// <summary>True when <paramref name="principal"/> carries an embed <c>auth_method</c>, verified or not.</summary>
    public static bool ClaimsToBeEmbed(ClaimsPrincipal? principal)
    {
        return principal != null
            && (principal.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.EmbedGrantAuthMethod)
                || principal.HasClaim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.EmbedTokenAuthMethod)
                || principal.Identity?.AuthenticationType == McpCallerClaimTypes.EmbedAuthenticationType);
    }

    public static string AuthMethodOf(EmbedCredential credential)
    {
        return credential == EmbedCredential.GrantSecret
            ? McpCallerClaimTypes.EmbedGrantAuthMethod
            : McpCallerClaimTypes.EmbedTokenAuthMethod;
    }
}
