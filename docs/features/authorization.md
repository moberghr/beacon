# Authorization and Permissions

Beacon provides a flexible, pluggable authorization system that supports both simple role-based access control (RBAC) and fine-grained resource-level permissions.

## Overview

The authorization system consists of:

- **IBeaconUserContext** - Provides access to current user information
- **IBeaconAuthorizationProvider** - Enforces authorization policies
- **Built-in Providers** - Ready-to-use implementations (Default, Role-Based, Database-Backed)
- **Custom Providers** - Plug in your own authorization logic

### Key Features

- Opt-in by default - Authorization disabled unless explicitly enabled
- Pluggable architecture - Integrate with any authentication system
- Multiple authorization levels - Global permissions and resource-level permissions
- Backward compatible - Existing installations work unchanged
- Framework agnostic - Works with ASP.NET Core Identity, OAuth, custom auth, etc.
- Database-backed roles - Built-in user management with Admin, Editor, Viewer roles

{: .note }
> For full user management (creating users, assigning roles, login form), see the [User Management Guide](user-management).

## Quick Start

### 1. Enable Authorization

Update your `Program.cs` to enable authorization:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187/beacon";

    // Enable authorization
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

builder.Services.AddBeaconUI();

// Enable authorization middleware
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin") // Your authentication
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/beacon");
```

### 2. Add Role Claims

The built-in `RoleBasedAuthorizationProvider` requires role claims. Add a claims transformer:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;

public class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add Beacon role claim
        identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        identity.AddClaim(new Claim(BeaconClaims.UserId, principal.Identity.Name));
        identity.AddClaim(new Claim(BeaconClaims.UserName, principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

// Register it
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();
```

### 3. Test Authorization

Start your application and verify:
- Username appears in top-right corner (not hardcoded)
- Unauthorized operations return 403 Forbidden
- Different roles have different permissions

## User Context

### Accessing Current User

Inject `IBeaconUserContext` anywhere in your application:

```csharp
public class MyService
{
    private readonly IBeaconUserContext _userContext;

    public MyService(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public void DoSomething()
    {
        var userId = _userContext.UserId;
        var userName = _userContext.UserName;
        var email = _userContext.Email;
        var isAuthenticated = _userContext.IsAuthenticated;

        if (_userContext.HasClaim(BeaconClaims.Role, "Admin"))
        {
            // Admin-only logic
        }
    }
}
```

### Standard Claims

Use these standard claim types for consistency:

```csharp
BeaconClaims.UserId       // "beacon:user_id"
BeaconClaims.UserName     // "beacon:user_name"
BeaconClaims.Role         // "beacon:role"
BeaconClaims.Permission   // "beacon:permission"
```

## Built-in Authorization Providers

### DefaultAuthorizationProvider

Allows all operations (backward compatible default).

**Use case:** When you don't need authorization or handle it elsewhere.

```csharp
// Authorization disabled (default behavior)
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // Authorization.Enabled = false by default
    options.AddBeaconScheduler<BeaconScheduler>();
})
.UsePostgreSql(connectionString, "beacon");
```

### RoleBasedAuthorizationProvider

Simple RBAC with three built-in roles:

| Role | Read | Write | Delete | Execute | Archive |
|------|------|-------|--------|---------|---------|
| **Admin** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Editor** | ✅ | ✅ | ❌ | ✅ | ✅ |
| **Viewer** | ✅ | ❌ | ❌ | ✅ | ❌ |
| **Guest** | ❌ | ❌ | ❌ | ❌ | ❌ |

**Use case:** Simple role-based permissions without complex logic.

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
```

**Required claims:**
```csharp
identity.AddClaim(new Claim(BeaconClaims.Role, "Admin")); // or "Editor", "Viewer"
```

### DatabaseAuthorizationProvider

Database-backed authorization that reads roles from Beacon's user management tables. This is the recommended provider when using the built-in [User Management](user-management) system.

| Role | Level | Read | Create/Edit/Execute | Delete/Archive |
|------|-------|------|---------------------|----------------|
| **Admin** | 3 | Yes | Yes | Yes |
| **Editor** | 2 | Yes | Yes | No |
| **Viewer** | 1 | Yes | No | No |

**Use case:** When using Beacon's built-in user management with login form and role assignment.

```csharp
options.Authorization.Enabled = true;

// Enable user management + database auth
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

options.UserManagement = new UserManagementOptions
{
    Enabled = true,
    AllowInternalUsers = true
};
```

No claims transformer needed - roles are loaded directly from the database.

{: .note }
> `DatabaseAuthorizationProvider` is automatically registered when user management is enabled. It also supports `IsSuperAdmin` to bypass all checks.

## Custom Authorization Provider

Implement `IBeaconAuthorizationProvider` to create custom authorization logic:

```csharp
using Beacon.Core.Authorization;

public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMyPermissionService _permissionService;

    public MyAuthorizationProvider(
        IBeaconUserContext userContext,
        IMyPermissionService permissionService)
    {
        _userContext = userContext;
        _permissionService = permissionService;
    }

    // Global permissions (required)
    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }

    // Resource-level permissions (optional - return null to skip)
    public async Task<AuthorizationResult?> AuthorizeAsync(
        ResourceType resourceType,
        int resourceId,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // Example: Check resource ownership
        if (resourceType == ResourceType.Query)
        {
            var query = await _dbContext.Queries.FindAsync(resourceId);
            if (query.CreatedByUserId == _userContext.UserId)
                return AuthorizationResult.Success();

            return AuthorizationResult.Failure("Not the owner");
        }

        // Return null to use global permissions only
        return null;
    }

    public Task<AuthorizationResult?> AuthorizeNewResourceAsync(
        ResourceType resourceType,
        PermissionAction action,
        object? resourceContext = null,
        CancellationToken cancellationToken = default)
    {
        // Check if user can create new resources
        return Task.FromResult<AuthorizationResult?>(null);
    }

    public async Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
        ResourceType resourceType,
        PermissionAction action,
        CancellationToken cancellationToken = default)
    {
        // Return null = user sees all
        // Return empty list = user sees nothing
        // Return specific IDs = user sees only those

        if (resourceType == ResourceType.DataSource)
        {
            // Return only data sources user has access to
            return await _dbContext.DataSources
                .Where(ds => ds.CreatedByUserId == _userContext.UserId)
                .Select(ds => ds.Id)
                .ToListAsync(cancellationToken);
        }

        return null; // No filtering
    }
}
```

Register your provider:

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

## Integration Examples

### ASP.NET Core Identity

```csharp
// Add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class IdentityToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map Identity roles to Beacon roles
        if (principal.IsInRole("Administrator"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        else if (principal.IsInRole("PowerUser"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Editor"));
        else
            identity.AddClaim(new Claim(BeaconClaims.Role, "Viewer"));

        // Add user claims
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue(ClaimTypes.NameIdentifier)));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

builder.Services.AddScoped<IClaimsTransformation, IdentityToBeaconClaimsTransformer>();
```

### OAuth 2.0 / OpenID Connect

```csharp
// Add authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddOpenIdConnect(options =>
{
    options.Authority = "https://your-identity-provider.com";
    options.ClientId = "your-client-id";
    options.ClientSecret = "your-client-secret";
    options.ResponseType = "code";
    options.SaveTokens = true;
});

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class OAuthToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map OAuth roles to Beacon roles
        var role = principal.FindFirstValue("role") switch
        {
            "admin" => "Admin",
            "user" => "Editor",
            _ => "Viewer"
        };

        identity.AddClaim(new Claim(BeaconClaims.Role, role));
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue("sub")));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.FindFirstValue("name")));

        return Task.FromResult(principal);
    }
}
```

### External Authorization Service

```csharp
// Your external auth service
public interface IExternalAuthService
{
    Task<bool> CheckPermissionAsync(string userId, string permission);
    Task<string[]> GetUserRolesAsync(string userId);
}

// Custom authorization provider
public class ExternalAuthProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IExternalAuthService _externalAuth;

    public ExternalAuthProvider(
        IBeaconUserContext userContext,
        IExternalAuthService externalAuth)
    {
        _userContext = userContext;
        _externalAuth = externalAuth;
    }

    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }

    // Implement other methods...
}

// Register it
builder.Services.AddScoped<IExternalAuthService, YourExternalAuthService>();
options.AddAuthorizationProvider<ExternalAuthProvider>();
```

## Resource Types and Actions

### Resource Types

```csharp
public enum ResourceType
{
    DataSource = 1,              // Database connections
    Query = 2,                   // SQL queries
    QueryFolder = 3,             // Query organization
    Subscription = 4,            // Scheduled query executions
    Recipient = 5,               // Notification recipients
    QueryTask = 6,               // Manual query tasks
    MigrationJob = 7,            // Data migration jobs
    DataSourceDocumentation = 8, // AI-generated documentation
    AiActor = 9,                 // AI monitoring agents
    AiActorPlan = 10,           // AI actor execution plans
    AiAlertConfiguration = 11    // AI-generated alerts
}
```

### Permission Actions

```csharp
public enum PermissionAction
{
    Read = 1,      // View resource
    Create = 2,    // Create new resource
    Update = 3,    // Modify existing resource
    Delete = 4,    // Permanently delete resource
    Execute = 5,   // Execute query/subscription
    Archive = 6,   // Archive resource (soft delete)
    Approve = 7,   // Approve AI Actor plans
    Lock = 8,      // Lock query from AI modifications
    Export = 9     // Export documentation/data
}
```

## Advanced Scenarios

### Resource-Level Authorization

Implement fine-grained permissions based on resource ownership:

```csharp
public async Task<AuthorizationResult?> AuthorizeAsync(
    ResourceType resourceType,
    int resourceId,
    PermissionAction action,
    CancellationToken cancellationToken = default)
{
    // Allow admins to do anything
    if (_userContext.HasClaim(BeaconClaims.Role, "Admin"))
        return AuthorizationResult.Success();

    // Check ownership for queries
    if (resourceType == ResourceType.Query)
    {
        var query = await _dbContext.Queries
            .Where(q => q.Id == resourceId)
            .Select(q => new { q.CreatedByUserId, q.IsShared })
            .FirstOrDefaultAsync(cancellationToken);

        if (query == null)
            return AuthorizationResult.Failure("Query not found");

        // Owner has full access
        if (query.CreatedByUserId == _userContext.UserId)
            return AuthorizationResult.Success();

        // Others can only read if shared
        if (query.IsShared && action == PermissionAction.Read)
            return AuthorizationResult.Success();

        return AuthorizationResult.Failure("Access denied");
    }

    return null; // Use global permissions
}
```

### Permission Caching

Cache permissions for better performance:

```csharp
public class CachedAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMemoryCache _cache;
    private readonly IActualAuthProvider _actualProvider;

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"write_perm_{_userContext.UserId}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _actualProvider.HasWritePermissionAsync(cancellationToken);
        });
    }
}
```

### Multi-Tenancy

Filter resources by tenant:

```csharp
public async Task<IEnumerable<int>?> GetAccessibleResourceIdsAsync(
    ResourceType resourceType,
    PermissionAction action,
    CancellationToken cancellationToken = default)
{
    var tenantId = _userContext.Metadata["TenantId"] as string;

    if (resourceType == ResourceType.DataSource)
    {
        return await _dbContext.DataSources
            .Where(ds => ds.TenantId == tenantId)
            .Select(ds => ds.Id)
            .ToListAsync(cancellationToken);
    }

    return null;
}
```

## Audit Trail (Future)

Optional audit fields are available on entities for future audit trail support:

```csharp
public abstract class AuditableBaseEntity : BaseEntity
{
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public string? ModifiedByUserId { get; set; }
    public string? ModifiedByUserName { get; set; }
}
```

**Note:** These fields are currently nullable and not automatically populated. A future release will include an EF Core interceptor to automatically populate these fields when authorization is enabled.

## Configuration Options

### Authorization Options

```csharp
public class AuthorizationOptions
{
    /// <summary>
    /// Enable authorization checks. Default: false
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Authorization provider type. If null, uses DefaultAuthorizationProvider.
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Enable resource-level authorization (requires provider support).
    /// Default: false (use global read/write only)
    /// </summary>
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}
```

### Example Configuration

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187/beacon";

    // Authorization configuration
    options.Authorization.Enabled = true;
    options.Authorization.EnableResourceLevelAuthorization = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");
```

## Troubleshooting

### Authorization Not Working

**Problem:** Users can access resources they shouldn't.

**Solution:**
1. Verify authorization is enabled:
   ```csharp
   options.Authorization.Enabled = true;
   ```

2. Verify `UseAuthorization()` is called:
   ```csharp
   app.UseBeaconUI()
       .UseBasicAuthentication("admin", "admin")
       .UseAuthorization() // ← This must be present
       .AddBlazorUI("/beacon");
   ```

3. Check claims are added correctly:
   ```csharp
   // Add logging to your claims transformer
   _logger.LogInformation("User {User} assigned role {Role}",
       principal.Identity.Name, role);
   ```

### Username Not Displayed

**Problem:** Top-right corner shows "Guest" or nothing.

**Solution:**
1. Verify `IBeaconUserContext` is registered (automatic with `AddBeaconUI()`)
2. Check that claims transformer adds `BeaconClaims.UserName`
3. Verify user is authenticated

### 403 Forbidden on All Requests

**Problem:** All requests return 403 Forbidden.

**Solution:**
1. Check authorization provider is returning `true` for authenticated users
2. Add logging to authorization provider:
   ```csharp
   _logger.LogWarning("Authorization denied for {User}: {Reason}",
       _userContext.UserName, result.FailureReason);
   ```
3. Verify claims transformer is executed

### Provider Not Called

**Problem:** Authorization provider methods never execute.

**Solution:**
1. Verify provider is registered:
   ```csharp
   options.AddAuthorizationProvider<MyAuthorizationProvider>();
   ```
2. Check authorization is enabled
3. Ensure `UseAuthorization()` middleware is added

## Security Best Practices

1. **Always validate on the server** - Client-side checks are for UX only
2. **Use HTTPS in production** - Protect credentials and session cookies
3. **Store role assignments in database** - Don't hardcode in claims transformer
4. **Implement rate limiting** - Prevent brute force attacks
5. **Audit authorization failures** - Log all 403 responses with context
6. **Use strong session management** - Implement proper timeout and renewal
7. **Validate resource ownership** - Don't rely on resource IDs alone
8. **Principle of least privilege** - Default to most restrictive permissions

## Migration from Previous Versions

If you're upgrading from a version without authorization:

1. **No changes required** - Authorization is disabled by default
2. **Opt-in when ready** - Enable authorization when you're ready
3. **No breaking changes** - Existing code continues to work
4. **Gradual adoption** - Start with global permissions, add resource-level later

## See Also

- [User Management](user-management) - Built-in user management with login form and role assignment
- [Admin Settings](admin-settings) - Runtime configuration (Admin-only)
- [Configuration Guide](../getting-started/configuration.md)
- [Quick Start](../getting-started/quick-start.md)
