namespace Semantico.Core.Authentication;

public class OidcAuthenticationOptions
{
    public bool Enabled { get; set; } = false;
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string CallbackPath { get; set; } = "/signin-oidc";
    public IList<string> Scopes { get; set; } = new List<string> { "openid", "profile", "email" };
    public string DefaultRoleName { get; set; } = "Viewer";
    public string DisplayName { get; set; } = "SSO";
    public string? McpJwksEndpoint { get; set; }
}
