using Microsoft.Extensions.Options;
using Beacon.Core.Mcp;

namespace Beacon.Core.Configuration;

/// <summary>
/// Which JWT callers may use the MCP endpoint, bound from <c>Beacon:Mcp:Callers</c>. Consumed by
/// <see cref="ConfiguredMcpCallerMapper"/>. An absent section accepts no JWT caller on MCP (fail closed):
/// user mode is off by default and there are no systems.
/// </summary>
public sealed class McpCallerOptions
{
    public const string SectionName = "Beacon:Mcp:Callers";

    /// <summary>User mode: a delegated (on-behalf-of) Entra user token.</summary>
    public McpUserCallerOptions Users { get; set; } = new();

    /// <summary>System mode: an app-only token (matched by <c>ClientId</c>) or a service user (matched by <c>ObjectId</c>).</summary>
    public List<McpSystemCallerOptions> Systems { get; set; } = [];
}

public sealed class McpUserCallerOptions
{
    /// <summary>Accept delegated user tokens at all. Default off.</summary>
    public bool Enabled { get; set; }

    /// <summary>App roles (<c>roles</c> claim) that admit a user. Any-of, combined with <see cref="RequiredGroups"/>.</summary>
    public List<string> RequiredRoles { get; set; } = [];

    /// <summary>Group object ids (<c>groups</c> claim) that admit a user. Any-of, combined with <see cref="RequiredRoles"/>.</summary>
    public List<string> RequiredGroups { get; set; } = [];

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

internal sealed class McpCallerOptionsValidator : IValidateOptions<McpCallerOptions>
{
    public ValidateOptionsResult Validate(string? name, McpCallerOptions options)
    {
        var failures = new List<string>();
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
}
