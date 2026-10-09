namespace Beacon.Core.Authentication;

public class OidcAuthenticationOptions
{
    public bool Enabled { get; set; } = false;
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public IList<string> Scopes { get; set; } = new List<string> { "openid", "profile", "email" };

    /// <summary>
    /// Role given to a user Beacon provisions on their first SSO sign-in. Unset (the default) provisions the user with
    /// no role: they can sign in but see nothing until an administrator assigns a role.
    /// </summary>
    public string? DefaultRoleName { get; set; }

    public string DisplayName { get; set; } = "SSO";
    public string? McpJwksEndpoint { get; set; }

    /// <summary>
    /// Entra tenant ids (<c>tid</c> claim) whose users may sign in. Required unless <see cref="AllowAnyTenant"/> is set.
    /// </summary>
    public IList<string> AllowedTenants { get; set; } = new List<string>();

    /// <summary>
    /// Explicit opt-in to skip the tenant check, for identity providers that issue no <c>tid</c> claim (the
    /// <see cref="Authority"/> then decides who can sign in). Default off.
    /// </summary>
    public bool AllowAnyTenant { get; set; }

    /// <summary>
    /// Rejects Entra B2B guests: an <c>acct</c> claim of <c>1</c>, or an <c>idp</c> claim naming another identity
    /// provider than the signing tenant. Default on.
    /// </summary>
    public bool BlockGuests { get; set; } = true;

    /// <summary>
    /// Group object ids (<c>groups</c> claim). When this or <see cref="RequiredRoles"/> is set, a user must hold at
    /// least one listed group or role.
    /// </summary>
    public IList<string> RequiredGroups { get; set; } = new List<string>();

    /// <summary>
    /// App roles (<c>roles</c> claim). When this or <see cref="RequiredGroups"/> is set, a user must hold at least one
    /// listed role or group.
    /// </summary>
    public IList<string> RequiredRoles { get; set; } = new List<string>();

    /// <summary>
    /// Serves the OIDC front-channel sign-out path (<c>/signout-oidc</c>). Default off: an anonymous GET there would
    /// otherwise end the user's Beacon session.
    /// </summary>
    public bool EnableFrontChannelLogout { get; set; }

    /// <summary>
    /// Fails startup when SSO is enabled without its required settings or without a tenant admission rule.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Authority)
            || string.IsNullOrWhiteSpace(ClientId)
            || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException(
                "Beacon:Authentication:Oidc is Enabled but Authority, ClientId, or ClientSecret is missing.");
        }

        if (!AllowAnyTenant && !AllowedTenants.Any(x => !string.IsNullOrWhiteSpace(x)))
        {
            throw new InvalidOperationException(
                "Beacon:Authentication:Oidc is Enabled but admits every tenant: set AllowedTenants to your tenant id(s), " +
                "or set AllowAnyTenant to true when your identity provider issues no tenant (tid) claim.");
        }
    }
}
