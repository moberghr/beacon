using Microsoft.Extensions.Configuration;
using Beacon.Core;
using Beacon.Core.HostDocs;
using Beacon.Core.Mcp;
using Beacon.MCP.HostEndpoints;

namespace Beacon.Gateway;

/// <summary>
/// What <see cref="BeaconMcpGatewayServiceCollectionExtensions.AddBeaconMcpGateway"/> wires: Beacon as the MCP gateway of
/// the host application, for the host's own users, with the host's endpoints as tools and its documents as grounding.
/// </summary>
public sealed class BeaconMcpGatewayOptions
{
    /// <summary>The tools a gateway serves by default: the document tools. The SQL tools need data sources a gateway has none of.</summary>
    public static IReadOnlyList<string> DefaultBuiltInTools { get; } = ["get_context", "search", "get_documentation"];

    /// <summary>The server instructions sent to clients unless <see cref="ServerInstructions"/> replaces them.</summary>
    public const string DefaultServerInstructions =
        "This is the application's MCP server. Its api_<name> tools call the application's own endpoints as the signed-in person, " +
        "with that person's permissions in the application, and return what the application returns (for many endpoints, search_api " +
        "finds one and call_api calls it). A tool that changes data says so; call it only when the person asked for that change. " +
        "get_context describes the application and lists its documents, search finds documents and tools by keyword, and " +
        "get_documentation reads one document (document=<path>).";

    private readonly List<Action<HostEndpointToolOptions>> _tools = [];
    private readonly List<Action<HostDocsOptions>> _docs = [];

    /// <summary>The host project: the Beacon project the tools, documents and callers belong to. Required.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>The built-in tools served (<see cref="DefaultBuiltInTools"/> by default; see <c>BeaconMcpOptions.BuiltInTools</c>).</summary>
    public ISet<string> BuiltInTools { get; } = new HashSet<string>(DefaultBuiltInTools, StringComparer.Ordinal);

    /// <summary>
    /// Keep the arguments and results of tool calls in Beacon's audit. Off by default: a tool's input and output can carry
    /// anything the host's endpoints return, so the audit keeps the shape of each call and never its content.
    /// </summary>
    public bool RetainToolContent { get; set; }

    /// <summary>The instructions sent to clients at <c>initialize</c>. Default <see cref="DefaultServerInstructions"/>.</summary>
    public string ServerInstructions { get; set; } = DefaultServerInstructions;

    internal Action<BeaconBuilder>? DatabaseConfiguration { get; private set; }

    internal BeaconMcpGatewayEntra? EntraConfiguration { get; private set; }

    internal Type? UserResolverType { get; private set; }

    internal IReadOnlyList<Action<HostEndpointToolOptions>> ToolConfigurations => _tools;

    internal IReadOnlyList<Action<HostDocsOptions>> DocsConfigurations => _docs;

    /// <summary>
    /// Beacon's own database (its <c>beacon</c> schema): the last call of the Beacon builder chain, for example
    /// <c>db =&gt; db.UsePostgreSql(sp =&gt; sp.GetRequiredService&lt;NpgsqlDataSource&gt;())</c> to share the host's data
    /// source. Required.
    /// </summary>
    public BeaconMcpGatewayOptions Database(Action<BeaconBuilder> configure)
    {
        DatabaseConfiguration = configure ?? throw new ArgumentNullException(nameof(configure));

        return this;
    }

    /// <summary>
    /// Accept delegated access tokens of the host's Entra registration, read from a configuration section with
    /// <c>TenantId</c>, <c>ClientId</c> and optionally <c>Instance</c> (Microsoft.Identity.Web's <c>AzureAd</c> section).
    /// </summary>
    public BeaconMcpGatewayEntra Entra(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return Entra(
            section["TenantId"] ?? string.Empty,
            section["ClientId"] ?? string.Empty,
            section["Instance"] is { Length: > 0 } instance ? instance : BeaconMcpGatewayEntra.DefaultInstance);
    }

    /// <summary>Accept delegated access tokens of the host's Entra registration <paramref name="clientId"/> in <paramref name="tenantId"/>.</summary>
    public BeaconMcpGatewayEntra Entra(string tenantId, string clientId, string instance = BeaconMcpGatewayEntra.DefaultInstance)
    {
        EntraConfiguration = new BeaconMcpGatewayEntra(tenantId, clientId, instance);

        return EntraConfiguration;
    }

    /// <summary>Who a token is in the host application, and what a tool call runs as (see <see cref="IMcpHostUserResolver"/>). Required.</summary>
    public BeaconMcpGatewayOptions Users<TResolver>()
        where TResolver : class, IMcpHostUserResolver
    {
        UserResolverType = typeof(TResolver);

        return this;
    }

    /// <summary>The host endpoint tools (<see cref="HostEndpointToolOptions"/>); the project name is set for you.</summary>
    public BeaconMcpGatewayOptions Tools(Action<HostEndpointToolOptions> configure)
    {
        _tools.Add(configure ?? throw new ArgumentNullException(nameof(configure)));

        return this;
    }

    /// <summary>Documents the host ships, as grounding (<see cref="HostDocsOptions"/>); the project name is set for you.</summary>
    public BeaconMcpGatewayOptions Docs(Action<HostDocsOptions> configure)
    {
        _docs.Add(configure ?? throw new ArgumentNullException(nameof(configure)));

        return this;
    }

    internal void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectName))
        {
            missing.Add("ProjectName");
        }

        if (DatabaseConfiguration == null)
        {
            missing.Add("Database(db => db.UsePostgreSql(...) or db.UseSqlServer(...))");
        }

        if (EntraConfiguration == null)
        {
            missing.Add("Entra(builder.Configuration.GetSection(\"AzureAd\"))");
        }

        if (UserResolverType == null)
        {
            missing.Add("Users<TResolver>() with your IMcpHostUserResolver");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"AddBeaconMcpGateway: set {string.Join(", ", missing)}.");
        }

        EntraConfiguration!.Validate();
    }
}

/// <summary>The host's Entra registration whose delegated access tokens the gateway accepts.</summary>
public sealed class BeaconMcpGatewayEntra
{
    public const string DefaultInstance = "https://login.microsoftonline.com/";

    internal BeaconMcpGatewayEntra(string tenantId, string clientId, string instance)
    {
        TenantId = tenantId.Trim();
        ClientId = clientId.Trim();
        Instance = instance.Trim().TrimEnd('/');
    }

    public string TenantId { get; }

    public string ClientId { get; }

    public string Instance { get; }

    /// <summary>The delegated scope a token must carry (its short name). Default <c>mcp.access</c>.</summary>
    public string Scope { get; private set; } = "mcp.access";

    /// <summary>When not empty, the only client applications whose tokens are accepted.</summary>
    public IReadOnlyList<string> AllowedClientIds => _allowedClientIds;

    private readonly List<string> _allowedClientIds = [];

    /// <summary>The issuers Beacon accepts: the tenant's v2 issuer (first, the one discovery publishes) and its v1 issuer.</summary>
    internal IReadOnlyList<string> Issuers => [$"{Instance}/{TenantId}/v2.0", $"https://sts.windows.net/{TenantId}/"];

    /// <summary>The audiences: the App ID URI a v1 token carries and the bare client id a v2 token carries.</summary>
    internal IReadOnlyList<string> Audiences => [$"api://{ClientId}", ClientId];

    internal string JwksEndpoint => $"{Instance}/{TenantId}/discovery/v2.0/keys";

    /// <summary><c>api://&lt;clientId&gt;/&lt;scope&gt;</c>, as a client requests it from Entra.</summary>
    public string QualifiedScope => $"api://{ClientId}/{Scope}";

    /// <summary>The delegated scope a token must carry, instead of <c>mcp.access</c>.</summary>
    public BeaconMcpGatewayEntra RequireScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        Scope = scope.Trim();

        return this;
    }

    /// <summary>Accept tokens only from these client applications (the MCP client's registration, say).</summary>
    public BeaconMcpGatewayEntra AllowClients(params string[] clientIds)
    {
        _allowedClientIds.AddRange(clientIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()));

        return this;
    }

    /// <summary>Accept tokens only from the client applications a configuration list names; an empty list accepts any.</summary>
    public BeaconMcpGatewayEntra AllowClients(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return AllowClients(section.GetChildren()
            .Select(x => x.Value)
            .OfType<string>()
            .ToArray());
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("AddBeaconMcpGateway: the Entra tenant id and client id are required (AzureAd:TenantId, AzureAd:ClientId).");
        }

        if (!Uri.TryCreate(Instance, UriKind.Absolute, out var instance) || instance.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("AddBeaconMcpGateway: the Entra instance must be an https URL.");
        }
    }
}
