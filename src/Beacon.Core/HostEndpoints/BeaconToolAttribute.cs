namespace Beacon.Core.HostEndpoints;

/// <summary>
/// Endpoint metadata that opts one host endpoint in as a Beacon MCP tool. Put <see cref="BeaconToolAttribute"/> on
/// a controller action or on a handler class whose framework copies class attributes into its endpoint's metadata
/// (Moberg Warp handlers), or call <c>.WithBeaconTool(...)</c> on a minimal API endpoint. Endpoints without it are
/// never exposed.
/// </summary>
public interface IBeaconToolMetadata
{
    /// <summary>The tool name, <c>^[a-z][a-z0-9_]{2,47}$</c>, unique per host. MCP exposes it as <c>api_&lt;name&gt;</c>.</summary>
    string Name { get; }

    /// <summary>What the endpoint returns, written for an agent. Falls back to the action's XML doc summary.</summary>
    string? Description { get; }

    /// <summary>
    /// The declared read-only flag. <c>true</c>: the endpoint changes nothing. <c>false</c>: a write tool, exposed only
    /// when the host opts in with <c>HostEndpointToolOptions.AllowWriteTools</c> (startup fails otherwise).
    /// </summary>
    bool ReadOnly { get; }

    /// <summary>
    /// True when <see cref="ReadOnly"/> was set explicitly. Beacon never infers read-only from the HTTP verb (hosts
    /// read over POST), so an undeclared flag fails startup.
    /// </summary>
    bool IsReadOnlyDeclared { get; }

    /// <summary>
    /// Write tools only: the endpoint may delete data or irreversibly overwrite it (MCP <c>destructiveHint</c>). Default
    /// <c>false</c>. A read-only tool that declares it fails startup. A default member, so existing implementations
    /// keep compiling and loading unchanged.
    /// </summary>
    bool Destructive => false;

    /// <summary>
    /// Write tools only: repeating the call with the same arguments has no further effect (MCP <c>idempotentHint</c>).
    /// Default <c>false</c>. Ignored for read-only tools, which are always idempotent.
    /// </summary>
    bool Idempotent => false;
}

/// <summary>
/// Exposes a controller action as the Beacon MCP tool <c>api_&lt;name&gt;</c>, invoked in-process as the MCP caller's
/// host principal, through the action's real model binding, filters and authorization policies. It also goes on a
/// handler class whose framework copies class attributes into the endpoint's metadata (a Moberg Warp handler with a
/// <c>[WarpHttp*]</c> route); on an MVC controller class it is refused at startup, since it would mark every action.
/// <c>ReadOnly</c> is mandatory: <c>true</c> is the host's declaration that the action changes nothing; <c>false</c>
/// declares a write tool, which also needs <c>HostEndpointToolOptions.AllowWriteTools</c>.
/// </summary>
/// <example><code>
/// [HttpPost]
/// [Permission(AdminPermission.ViewLoans)]
/// [BeaconTool("customer_loans", Description = "Loans of one customer, newest first.", ReadOnly = true)]
/// public IActionResult CustomerLoans([FromForm] CustomerLoansFilter filter) { ... }
/// </code></example>
/// <example><code>
/// [Authorize]
/// [BeaconTool("my_assets", Description = "The signed-in user's assets, newest first.", ReadOnly = true)]
/// [WarpHttpGet("/api/my/assets")]
/// public sealed class ListMyAssetsQueryHandler(...) : IRequestHandler&lt;ListMyAssetsQuery, IReadOnlyList&lt;AssetListItem&gt;&gt; { ... }
/// </code></example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class BeaconToolAttribute(string name) : Attribute, IBeaconToolMetadata
{
    private bool _readOnly;

    public string Name { get; } = name;

    public string? Description { get; set; }

    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            IsReadOnlyDeclared = true;
        }
    }

    public bool IsReadOnlyDeclared { get; private set; }

    /// <inheritdoc />
    public bool Destructive { get; set; }

    /// <inheritdoc />
    public bool Idempotent { get; set; }
}

/// <summary>The metadata <c>.WithBeaconTool(...)</c> attaches to a minimal API endpoint.</summary>
public sealed class BeaconToolMetadata(string name, string? description, bool readOnly) : IBeaconToolMetadata
{
    public string Name { get; } = name;

    public string? Description { get; } = description;

    public bool ReadOnly { get; } = readOnly;

    public bool IsReadOnlyDeclared => true;

    /// <inheritdoc />
    public bool Destructive { get; init; }

    /// <inheritdoc />
    public bool Idempotent { get; init; }
}
