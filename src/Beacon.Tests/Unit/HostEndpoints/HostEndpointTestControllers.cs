using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Beacon.Core.HostEndpoints;

namespace Beacon.Tests.Unit.HostEndpoints;

public sealed record ThingDto(int Id, string Name);

public sealed class ThingSearchRequest
{
    [Description("Case-insensitive part of the name.")]
    public string? NameContains { get; set; }

    public int Take { get; set; } = 10;
}

public enum LoanStatus
{
    Active,
    Closed
}

public sealed class LoanFilter
{
    [Required]
    public int CustomerId { get; set; }

    public LoanStatus? Status { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateRange? Period { get; set; }
}

public sealed class DateRange
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }
}

/// <summary>A Kendo-style grid request bound by a custom model binder from raw form fields.</summary>
public sealed class GridRequest
{
    public int Page { get; set; }

    public int PageSize { get; set; }

    public string? Sort { get; set; }
}

public sealed class GridRequestBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var form = bindingContext.HttpContext.Request.Form;
        bindingContext.Result = ModelBindingResult.Success(new GridRequest
        {
            Page = int.TryParse(form["page"], out var page) ? page : 1,
            PageSize = int.TryParse(form["pageSize"], out var size) ? size : 20,
            Sort = form["sort"]
        });

        return Task.CompletedTask;
    }
}

[ApiController]
[Route("things")]
public sealed class ThingsController(IHttpContextAccessor accessor) : ControllerBase
{
    public static readonly ThingDto[] Things = [new(1, "Anchor"), new(2, "Beacon"), new(3, "Compass")];

    [HttpPost("search")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_search", Description = "Search things by name.", ReadOnly = true)]
    public ActionResult<List<ThingDto>> Search([FromBody] ThingSearchRequest request) =>
        Things
            .Where(x => request.NameContains == null || x.Name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase))
            .Take(request.Take)
            .ToList();

    [HttpGet("{id:int}")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_by_id", Description = "One thing, echoing what the endpoint saw.", ReadOnly = true)]
    public IActionResult Get(int id, [FromQuery] string? include, [FromHeader(Name = "X-Tenant")] string? tenant) =>
        Ok(new
        {
            id,
            include,
            tenant,
            path = Request.Path.Value,
            authorization = Request.Headers.Authorization.ToString(),
            cookie = Request.Headers.Cookie.ToString(),
            user = User.Identity?.Name,
            authenticationType = User.Identity?.AuthenticationType,
            accessorUser = accessor.HttpContext?.User.Identity?.Name,
            claims = User.Claims.Select(x => x.Type + "=" + x.Value).ToArray()
        });

    [HttpGet("big")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_big", ReadOnly = true)]
    public IActionResult Big() => Ok(new { data = new string('x', 10_000) });

    [HttpGet("slow")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_slow", ReadOnly = true)]
    public async Task<IActionResult> Slow()
    {
        // Ignores RequestAborted on purpose: the dispatcher must still return on time.
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

        return Ok(new { done = true });
    }

    [HttpGet("file")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_file", ReadOnly = true)]
    public IActionResult File() => File(new byte[] { 1, 2, 3 }, "application/octet-stream");

    [HttpGet("text")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_text", ReadOnly = true)]
    public IActionResult Text() => Content("plain words", "text/plain");

    [HttpGet("missing")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_missing", ReadOnly = true)]
    public IActionResult Missing() => NotFound(new { error = "no such thing" });

    [HttpGet("throws")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("thing_throws", ReadOnly = true)]
    public IActionResult Throws() => throw new InvalidOperationException("secret row data in the message");

    [HttpGet("unmarked")]
    [Authorize(Policy = HostEndpointTestHost.ViewThingPolicy)]
    public IActionResult Unmarked() => Ok();
}

/// <summary>A Web.Admin-style MVC controller: no [ApiController], reads over POST with form binding and antiforgery.</summary>
[Route("admin/loans")]
public sealed class AdminLoansController : Controller
{
    /// <summary>Loans of one customer.</summary>
    [HttpPost("grid")]
    [Authorize(Policy = HostEndpointTestHost.ViewLoansPolicy)]
    [ValidateAntiForgeryToken]
    [BeaconTool("loan_grid", ReadOnly = true)]
    public IActionResult Grid(LoanFilter filter) =>
        Json(new
        {
            filter.CustomerId,
            status = filter.Status?.ToString(),
            filter.Tags,
            from = filter.Period?.From?.ToString("yyyy-MM-dd"),
            valid = ModelState.IsValid
        });

    [HttpPost("public")]
    [AllowAnonymous]
    [BeaconTool("loan_public", Description = "An anonymous read.", ReadOnly = true)]
    public IActionResult Public(string? q) => Json(new { q });

    [HttpPost("page")]
    [Authorize(Policy = HostEndpointTestHost.ViewLoansPolicy)]
    [BeaconTool("loan_page", Description = "A grid page.", ReadOnly = true)]
    public IActionResult Page([ModelBinder(typeof(GridRequestBinder))] GridRequest request) =>
        Json(new { request.Page, request.PageSize, request.Sort });
}

public sealed class MissingReadOnlyController : ControllerBase
{
    [HttpGet("bad/readonly-missing")]
    [Authorize]
    [BeaconTool("missing_readonly")]
    public IActionResult Get() => Ok();
}

public sealed class FalseReadOnlyController : ControllerBase
{
    [HttpGet("bad/readonly-false")]
    [Authorize]
    [BeaconTool("false_readonly", ReadOnly = false)]
    public IActionResult Get() => Ok();
}

public sealed class BadNameController : ControllerBase
{
    [HttpGet("bad/name")]
    [Authorize]
    [BeaconTool("Bad-Name", ReadOnly = true)]
    public IActionResult Get() => Ok();
}

public sealed class DuplicateNameController : ControllerBase
{
    [HttpGet("bad/one")]
    [Authorize]
    [BeaconTool("same_name", ReadOnly = true)]
    public IActionResult One() => Ok();

    [HttpGet("bad/two")]
    [Authorize]
    [BeaconTool("same_name", ReadOnly = true)]
    public IActionResult Two() => Ok();
}

public sealed class NoAuthorizationController : ControllerBase
{
    [HttpGet("bad/no-auth")]
    [BeaconTool("no_auth", ReadOnly = true)]
    public IActionResult Get() => Ok();
}

public sealed class StepUpSchemeController : ControllerBase
{
    public const string Scheme = "StepUp";

    [HttpGet("bad/step-up")]
    [Authorize(AuthenticationSchemes = Scheme, Policy = HostEndpointTestHost.ViewThingPolicy)]
    [BeaconTool("step_up", ReadOnly = true)]
    public IActionResult Get() => Ok();
}

internal static class HostEndpointTestClaims
{
    public static Claim Permission(string value) => new(HostEndpointTestHost.PermissionClaim, value);
}

/// <summary>
/// Netgiro's <c>[Permission]</c> shape: an authorization filter that reads the AMBIENT principal
/// (<c>Thread.CurrentPrincipal</c>, set by host middleware), not <c>HttpContext.User</c>.
/// </summary>
public sealed class AmbientPermissionAttribute(string permission) : Attribute, Microsoft.AspNetCore.Mvc.Filters.IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext context)
    {
        if (Thread.CurrentPrincipal is not ClaimsPrincipal principal || !principal.HasClaim(HostEndpointTestHost.PermissionClaim, permission))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }

        return Task.CompletedTask;
    }
}

[Authorize]
[Route("ambient")]
public sealed class AmbientController : Controller
{
    [HttpGet("loans")]
    [AmbientPermission("loans.ambient")]
    [BeaconTool("ambient_loans", ReadOnly = true)]
    public IActionResult Loans() => Json(new { user = (Thread.CurrentPrincipal as ClaimsPrincipal)?.Identity?.Name });
}

/// <summary>What Netgiro's middleware does (<c>Identity.Set(principal); await next();</c>).</summary>
public sealed class AmbientPrincipalMiddleware : IHostEndpointDispatchMiddleware
{
    public async Task InvokeAsync(HttpContext syntheticContext, Beacon.Core.Mcp.McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
    {
        // Genuinely asynchronous before the ambient state is set: it must still reach the endpoint's filters.
        await Task.Delay(10, cancellationToken);
        Thread.CurrentPrincipal = syntheticContext.User;
        await next();
    }
}

/// <summary>Records the order it ran in, then continues.</summary>
public sealed class OrderRecordingMiddleware(string name, List<string> order) : IHostEndpointDispatchMiddleware
{
    public async Task InvokeAsync(HttpContext syntheticContext, Beacon.Core.Mcp.McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
    {
        order.Add(name);
        await next();
    }
}

public sealed class ShortCircuitMiddleware : IHostEndpointDispatchMiddleware
{
    public Task InvokeAsync(HttpContext syntheticContext, Beacon.Core.Mcp.McpCaller caller, Func<Task> next, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class ThrowingMiddleware : IHostEndpointDispatchMiddleware
{
    public async Task InvokeAsync(HttpContext syntheticContext, Beacon.Core.Mcp.McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
    {
        await Task.Yield();
        throw new InvalidOperationException("middleware detail that must not leak");
    }
}
