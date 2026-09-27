using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;
using Beacon.Core.Mcp;

namespace Beacon.Core.Configuration;

/// <summary>
/// Which JWT callers may use the MCP endpoint, bound from <c>Beacon:Mcp:Callers</c>. Consumed by
/// <see cref="ConfiguredMcpCallerMapper"/>. An absent section accepts no JWT caller on MCP (fail closed):
/// user mode is off by default and there are no systems.
/// <para>
/// When callers are active (<see cref="IsActive"/>) the host refuses to start unless <see cref="AllowedTenants"/> is set
/// and bearer JWT validation checks both issuer and audience (<see cref="McpCallerOptionsValidator"/>).
/// </para>
/// </summary>
public sealed class McpCallerOptions
{
    public const string SectionName = "Beacon:Mcp:Callers";

    /// <summary>
    /// Entra tenant ids (<c>tid</c>) whose tokens may be mapped to a caller. Required and non-empty when callers are
    /// active; a token from any other tenant, or without <c>tid</c>, is rejected.
    /// </summary>
    public List<string> AllowedTenants { get; set; } = [];

    /// <summary>User mode: a delegated (on-behalf-of) Entra user token.</summary>
    public McpUserCallerOptions Users { get; set; } = new();

    /// <summary>System mode: an app-only token (matched by <c>ClientId</c>) or a service user (matched by <c>ObjectId</c>).</summary>
    public List<McpSystemCallerOptions> Systems { get; set; } = [];

    /// <summary>True when any JWT caller can be admitted: user mode is on or at least one system is configured.</summary>
    public bool IsActive => Users.Enabled || Systems.Count > 0;
}

public sealed class McpUserCallerOptions
{
    /// <summary>Accept delegated user tokens at all. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>App roles (<c>roles</c> claim) that admit a user. Any-of, combined with <see cref="RequiredGroups"/>.</summary>
    public List<string> RequiredRoles { get; set; } = [];

    /// <summary>Group object ids (<c>groups</c> claim) that admit a user. Any-of, combined with <see cref="RequiredRoles"/>.</summary>
    public List<string> RequiredGroups { get; set; } = [];

    /// <summary>
    /// Explicit opt-in to admit every user of the allowed tenants when both <see cref="RequiredRoles"/> and
    /// <see cref="RequiredGroups"/> are empty. Without it, that combination fails startup. Default off.
    /// </summary>
    public bool AllowAnyTenantUser { get; set; }

    /// <summary>Projects granted to every accepted user.</summary>
    public List<int> ProjectIds { get; set; } = [];

    /// <summary>Group id or app role → extra project ids. Additive to <see cref="ProjectIds"/>.</summary>
    public Dictionary<string, List<int>> GroupProjects { get; set; } = [];

    public McpCallerScope Scope { get; set; } = McpCallerScope.Execute;

    /// <summary>Find or create a Beacon user keyed on the Entra <c>oid</c> so audit rows carry a user id.</summary>
    public bool AutoProvision { get; set; } = true;

    /// <summary>Role given to a user the first time <see cref="AutoProvision"/> creates it.</summary>
    public string DefaultRoleName { get; set; } = "Viewer";
}

public sealed class McpSystemCallerOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Matches <c>azp</c>/<c>appid</c> of an app-only (client credentials) token. Mutually exclusive with <see cref="ObjectId"/>.</summary>
    public string? ClientId { get; set; }

    /// <summary>Matches the <c>oid</c> of a service user or service principal. Mutually exclusive with <see cref="ClientId"/>.</summary>
    public string? ObjectId { get; set; }

    public List<int> ProjectIds { get; set; } = [];

    public McpCallerScope Scope { get; set; } = McpCallerScope.Execute;

    /// <summary>The permission set this identity gets inside the host application (used for in-process API dispatch).</summary>
    public List<McpHostClaimOptions> HostClaims { get; set; } = [];
}

public sealed class McpHostClaimOptions
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <param name="jwtOptions">
/// The bearer JWT options the host registered (<c>AddBeaconJwtAuthentication</c>), or null when bearer JWT is not
/// configured at all.
/// </param>
internal sealed class McpCallerOptionsValidator(JwtAuthenticationOptions? jwtOptions = null) : IValidateOptions<McpCallerOptions>
{
    public ValidateOptionsResult Validate(string? name, McpCallerOptions options)
    {
        var failures = new List<string>();

        if (options.IsActive)
        {
            ValidateActiveCallers(options, failures);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Systems.Count; i++)
        {
            var system = options.Systems[i];
            var path = $"{McpCallerOptions.SectionName}:Systems:{i}";

            if (string.IsNullOrWhiteSpace(system.Name))
            {
                failures.Add($"{path}:Name is required.");
            }
            else if (!names.Add(system.Name))
            {
                failures.Add($"{path}:Name '{system.Name}' is used by more than one system caller.");
            }

            var hasClientId = !string.IsNullOrWhiteSpace(system.ClientId);
            var hasObjectId = !string.IsNullOrWhiteSpace(system.ObjectId);
            if (hasClientId == hasObjectId)
            {
                failures.Add($"{path} must set exactly one of ClientId or ObjectId.");
            }

            var identity = hasClientId ? $"client:{system.ClientId}" : $"object:{system.ObjectId}";
            if ((hasClientId || hasObjectId) && !identities.Add(identity))
            {
                failures.Add($"{path} matches the same ClientId/ObjectId as another system caller.");
            }

            if (system.ProjectIds.Any(x => x <= 0))
            {
                failures.Add($"{path}:ProjectIds must contain only positive project ids.");
            }

            if (system.HostClaims.Any(x => string.IsNullOrWhiteSpace(x.Type)))
            {
                failures.Add($"{path}:HostClaims entries must set Type.");
            }
        }

        var users = options.Users;
        var userProjectIds = users.ProjectIds.Concat(users.GroupProjects.Values.SelectMany(x => x));
        if (userProjectIds.Any(x => x <= 0))
        {
            failures.Add($"{McpCallerOptions.SectionName}:Users project ids must be positive.");
        }

        if (users.Enabled && users.AutoProvision && string.IsNullOrWhiteSpace(users.DefaultRoleName))
        {
            failures.Add($"{McpCallerOptions.SectionName}:Users:DefaultRoleName is required when AutoProvision is on.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    // Callers can be admitted: a token's signature alone must never be enough. Issuer and audience validation have to
    // be on with explicit values (Entra's JWKS signs tokens for every tenant and every resource), the tenant list has
    // to be explicit, and user mode must not silently admit a whole tenant.
    private void ValidateActiveCallers(McpCallerOptions options, List<string> failures)
    {
        var section = McpCallerOptions.SectionName;

        if (options.AllowedTenants.Count == 0 || options.AllowedTenants.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{section}:AllowedTenants must list the Entra tenant id(s) whose tokens are accepted when MCP callers are configured.");
        }

        var users = options.Users;
        if (users.Enabled && users.RequiredRoles.Count == 0 && users.RequiredGroups.Count == 0 && !users.AllowAnyTenantUser)
        {
            failures.Add($"{section}:Users has no RequiredRoles or RequiredGroups, which admits every user of the tenant. Set RequiredRoles/RequiredGroups, or set Users:AllowAnyTenantUser = true to allow that explicitly.");
        }

        if (jwtOptions == null || !jwtOptions.EnableBearerAuthentication)
        {
            failures.Add($"{section} is configured but bearer JWT authentication is not enabled (AddBeaconJwtAuthentication with EnableBearerAuthentication = true).");
            return;
        }

        var validation = jwtOptions.Validation;
        if (!validation.ValidateIssuer || validation.EffectiveIssuers().Count == 0)
        {
            failures.Add($"{section} requires bearer JWT issuer validation: set Validation.ValidIssuer (or ValidIssuers) and keep ValidateIssuer on.");
        }

        if (!validation.ValidateAudience || validation.EffectiveAudiences().Count == 0)
        {
            failures.Add($"{section} requires bearer JWT audience validation: set Validation.ValidAudience (or ValidAudiences) to Beacon's app registration and keep ValidateAudience on.");
        }
    }
}
