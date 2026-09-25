namespace Beacon.Core.HostEndpoints;

/// <summary>
/// Endpoint metadata that opts one host endpoint in as a Beacon MCP tool. Put <see cref="BeaconToolAttribute"/> on
/// a controller action, or call <c>.WithBeaconTool(...)</c> on a minimal API endpoint. Endpoints without it are
/// never exposed.
/// </summary>
public interface IBeaconToolMetadata
{
    /// <summary>The tool name, <c>^[a-z][a-z0-9_]{2,47}$</c>, unique per host. MCP exposes it as <c>api_&lt;name&gt;</c>.</summary>
    string Name { get; }

    /// <summary>What the endpoint returns, written for an agent. Falls back to the action's XML doc summary.</summary>
    string? Description { get; }

    /// <summary>The declared read-only flag. v1 only exposes endpoints that declare <c>true</c>.</summary>
    bool ReadOnly { get; }

    /// <summary>
    /// True when <see cref="ReadOnly"/> was set explicitly. Beacon never infers read-only from the HTTP verb (hosts
    /// read over POST), so an undeclared flag fails startup like a <c>false</c> one.
    /// </summary>
    bool IsReadOnlyDeclared { get; }
}

/// <summary>
/// Exposes a controller action as the Beacon MCP tool <c>api_&lt;name&gt;</c>, invoked in-process as the MCP caller's
/// host principal, through the action's real model binding, filters and authorization policies.
/// <c>ReadOnly = true</c> is mandatory: it is the host's declaration that the action changes nothing.
/// </summary>
/// <example><code>
/// [HttpPost]
/// [Permission(AdminPermission.ViewLoans)]
/// [BeaconTool("customer_loans", Description = "Loans of one customer, newest first.", ReadOnly = true)]
/// public IActionResult CustomerLoans([FromForm] CustomerLoansFilter filter) { ... }
/// </code></example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
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
}

/// <summary>The metadata <c>.WithBeaconTool(...)</c> attaches to a minimal API endpoint.</summary>
public sealed class BeaconToolMetadata(string name, string? description, bool readOnly) : IBeaconToolMetadata
{
    public string Name { get; } = name;

    public string? Description { get; } = description;

    public bool ReadOnly { get; } = readOnly;

    public bool IsReadOnlyDeclared => true;
}
