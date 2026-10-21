using System.Collections.Concurrent;
using System.Security.Claims;
using Beacon.Core.Mcp;

namespace Beacon.SampleGateway;

internal static class SampleAuthorization
{
    public const string CustomerIdClaim = "customer_id";
    public const string RoleClaim = "orders_role";
    public const string GatewayAdmin = "orders.gateway-admin";
}

/// <summary>
/// Who an MCP token is in this application. A real host reads its users table by the token's object id, refuses an id that
/// more than one active user carries, and never creates a user here. The sample knows two customers by fixed object ids.
/// </summary>
internal sealed class SampleMcpUsers(IConfiguration configuration) : IMcpHostUserResolver
{
    public Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken)
    {
        // "Sample:Customers:<object id>" = customer id, e.g. from user secrets.
        var customerId = configuration[$"Sample:Customers:{token.ObjectId}"];
        McpHostUser? user = customerId == null
            ? null
            : new McpHostUser(customerId, $"Customer {customerId}", [new Claim(SampleAuthorization.CustomerIdClaim, customerId)]);

        return Task.FromResult(user);
    }
}

internal sealed record OrderLine(string Product, int Quantity, decimal Price);

internal sealed record Order(int Id, int CustomerId, DateTime PlacedAt, string Status, decimal Total, IReadOnlyList<OrderLine> Lines);

/// <summary>The application's data, in memory for the sample.</summary>
internal sealed class OrderStore
{
    private readonly ConcurrentDictionary<int, Order> _orders = new(new Dictionary<int, Order>
    {
        [1] = new(1, 1, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), "Shipped", 42.50m, [new("Lamp", 1, 42.50m)]),
        [2] = new(2, 1, new DateTime(2026, 10, 18, 14, 30, 0, DateTimeKind.Utc), "Open", 19.90m, [new("Cable", 2, 9.95m)]),
        [3] = new(3, 2, new DateTime(2026, 10, 20, 8, 15, 0, DateTimeKind.Utc), "Open", 120m, [new("Chair", 1, 120m)])
    });

    public IReadOnlyList<Order> Of(int customerId) =>
        _orders.Values
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.PlacedAt)
            .ToList();

    public Order? Find(int customerId, int id) =>
        _orders.TryGetValue(id, out var order) && order.CustomerId == customerId ? order : null;

    public bool Cancel(int customerId, int id)
    {
        var order = Find(customerId, id);

        return order is { Status: "Open" } && _orders.TryUpdate(id, order with { Status = "Cancelled" }, order);
    }
}
