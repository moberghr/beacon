using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Beacon.Core.PostgreSql;
using Beacon.Gateway;
using Beacon.MCP.HostEndpoints;
using Beacon.SampleGateway;

// The reference MCP gateway host: an orders application whose customers ask Claude about their own orders. Everything
// Beacon-specific is the AddBeaconMcpGateway block, the two pipeline calls and the [BeaconTool]/WithBeaconTool marks.
var builder = WebApplication.CreateBuilder(args);

// The application's own sign-in. A real host uses its identity provider (Microsoft.Identity.Web for Entra); the sample signs
// a demo person in at /signin, in Development only. Register any IClaimsTransformation of your own BEFORE the gateway.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(x => x.LoginPath = "/signin");
builder.Services.AddAuthorization(x => x.AddPolicy(SampleAuthorization.GatewayAdmin, y => y.RequireClaim(SampleAuthorization.RoleClaim, "admin")));
builder.Services.AddSingleton<OrderStore>();

builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
{
    x.ProjectName = "orders";
    x.Database(db => db.UsePostgreSql());                         // ConnectionStrings:BeaconContext, schema "beacon"
    x.Entra(builder.Configuration.GetSection("AzureAd"))          // TenantId, ClientId of the application's registration
        .AllowClients(builder.Configuration.GetSection("McpAccess:AllowedClientIds"));
    x.Users<SampleMcpUsers>();                                    // who a token is in this application
    x.Tools(y =>
    {
        y.AllowWriteTools = true;                                  // cancel_order changes data: clients ask before calling it
        y.BindFromCaller("customerId", SampleAuthorization.CustomerIdClaim);
    });
    x.Docs(y => y.FromDirectory("docs", "**/*.md"));
    x.AdminUi(y =>
    {
        y.HostPolicy = SampleAuthorization.GatewayAdmin;
        y.HostUserId = z => z.FindFirst(SampleAuthorization.CustomerIdClaim)?.Value;
        y.SignInPath = "/signin";
    });
});

var app = builder.Build();

// A sample with one instance migrates at startup; a real host runs BeaconPostgreSqlMigrator from its migration step.
if (app.Environment.IsDevelopment())
{
    await BeaconPostgreSqlMigrator.MigrateAsync(builder.Configuration.GetConnectionString("BeaconContext")!);
}

app.UseBeaconMcpGateway();   // before UseAuthentication: bearer tokens on /beacon/mcp only
app.UseStaticFiles();        // Beacon's admin shell and its assets
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/my/orders", (int customerId, OrderStore orders) => orders.Of(customerId))
    .RequireAuthorization()
    .WithBeaconTool("my_orders", "The signed-in customer's orders, newest first: id, placedAt (UTC), status (Open, Shipped, Cancelled) and total.", readOnly: true);

app.MapGet("/api/my/orders/{id:int}", (int id, int customerId, OrderStore orders) =>
        orders.Find(customerId, id) is { } order ? Results.Ok(order) : Results.NotFound())
    .RequireAuthorization()
    .WithBeaconTool("my_order", "One of the signed-in customer's orders by id (from my_orders), with its lines. Another customer's order is not found.", readOnly: true);

app.MapPost("/api/my/orders/{id:int}/cancel", (int id, int customerId, OrderStore orders) =>
        orders.Cancel(customerId, id) ? Results.NoContent() : Results.Conflict(new { title = "Only an open order of yours can be cancelled." }))
    .RequireAuthorization()
    .WithBeaconTool("cancel_order", "Cancels one of the signed-in customer's open orders. Call it only when the customer asked for this order to be cancelled.", readOnly: false, destructive: false, idempotent: true);

if (app.Environment.IsDevelopment())
{
    // The demo sign-in: /signin?customer=1 signs in customer 1 (an admin of the gateway), /signin?customer=2 a customer.
    app.MapGet("/signin", async (HttpContext context, int customer = 1, string? returnUrl = null) =>
    {
        var claims = new List<Claim>
        {
            new(SampleAuthorization.CustomerIdClaim, customer.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, $"Customer {customer}"),
            new(ClaimTypes.Email, $"customer{customer}@example.com")
        };
        if (customer == 1)
        {
            claims.Add(new Claim(SampleAuthorization.RoleClaim, "admin"));
        }

        await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

        return Results.LocalRedirect(returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') ? returnUrl : "/beacon");
    }).AllowAnonymous();
}

app.MapBeaconMcpGateway();   // /beacon/mcp, discovery, and the admin UI at /beacon

app.Run();
