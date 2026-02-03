# Authorization Example Setup

This document explains the authorization implementation in the Semantico Sample Project.

## Overview

The sample project demonstrates a complete authorization system with:
- **Role-Based Access Control (RBAC)** with three roles: Admin, Editor, Viewer
- **Claims Transformation** to add role claims after authentication
- **Custom Authorization Provider** that enforces permissions
- **User Context** accessible throughout the application

## User Roles

The system supports three roles with different permission levels:

| Role | Read | Write | Delete | Description |
|------|------|-------|--------|-------------|
| **Admin** | ✅ | ✅ | ✅ | Full access to all features |
| **Editor** | ✅ | ✅ | ❌ | Can create and modify resources |
| **Viewer** | ✅ | ❌ | ❌ | Read-only access |

## Testing Authorization

The Basic Authentication middleware accepts any username/password combination. The role is determined by the username:

### Test Users

| Username | Password | Role | Permissions |
|----------|----------|------|-------------|
| `admin` | `admin` | Admin | Full access (read, write, delete) |
| `editor` | `editor` | Editor | Read and write (no delete) |
| `viewer` | `viewer` | Viewer | Read-only |
| Any other | Any | Viewer | Read-only (default) |

### How to Test

1. **Start the application:**
   ```bash
   dotnet run --project Semantico.SampleProject
   ```

2. **Navigate to:** `https://localhost:7187/semantico`

3. **Login with different users:**
   - Try `admin` / `admin` - You should see full functionality
   - Try `editor` / `editor` - You should be able to create/modify but not delete
   - Try `viewer` / `viewer` - You should only be able to view data
   - Try `test` / `test` - Should default to Viewer role

4. **Observe the behavior:**
   - The username displayed in the top-right corner changes based on who's logged in
   - Different roles have different permissions enforced by the middleware

## Implementation Details

### 1. Core Configuration (`Program.cs`)

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.BaseUrl = "https://localhost:7187/semantico";
    options.UseAI = true;

    // Enable authorization
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<SampleAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "semantico");
```

### 2. Claims Transformation (`SampleClaimsTransformation.cs`)

The claims transformer adds Semantico-specific claims after authentication:

```csharp
public class SampleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var username = principal.Identity?.Name;

        // Assign role based on username (for demo)
        var role = username?.ToLowerInvariant() switch
        {
            "admin" => "Admin",
            "editor" => "Editor",
            "viewer" => "Viewer",
            _ => "Viewer"
        };

        // Add Semantico claims
        identity.AddClaim(new Claim(SemanticoClaims.Role, role));
        // ... more claims

        return Task.FromResult(principal);
    }
}
```

### 3. Authorization Provider (`SampleAuthorizationProvider.cs`)

The authorization provider enforces permissions based on user roles:

```csharp
public class SampleAuthorizationProvider : ISemanticoAuthorizationProvider
{
    private readonly ISemanticoUserContext _userContext;

    public Task<bool> HasReadPermissionAsync(...)
    {
        // All authenticated users can read
        return Task.FromResult(_userContext.IsAuthenticated);
    }

    public Task<bool> HasWritePermissionAsync(...)
    {
        // Only admin and editor can write
        return Task.FromResult(
            _userContext.IsAuthenticated &&
            _userContext.UserName?.Equals("admin", ...) == true);
    }
}
```

### 4. Middleware Pipeline

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // Enforces authorization checks
    .AddBlazorUI("/semantico");
```

## Authorization Flow

```
┌─────────────────┐
│ User Requests   │
│ /semantico/*    │
└────────┬────────┘
         │
         ▼
┌─────────────────────────┐
│ BasicAuthMiddleware     │
│ - Validates credentials │
│ - Creates ClaimsPrincipal│
└────────┬────────────────┘
         │
         ▼
┌──────────────────────────────┐
│ SampleClaimsTransformation   │
│ - Adds SemanticoClaims.Role  │
│ - Adds user metadata         │
└────────┬─────────────────────┘
         │
         ▼
┌──────────────────────────────────┐
│ SemanticoAuthorizationMiddleware │
│ - Checks if authorized enabled   │
│ - Calls IAuthorizationProvider   │
│ - Returns 403 if unauthorized    │
└────────┬─────────────────────────┘
         │
         ▼
┌─────────────────┐
│ Blazor UI       │
│ Shows username  │
└─────────────────┘
```

## Production Considerations

### Security Recommendations

1. **Replace Basic Authentication** with a proper authentication system:
   - OAuth 2.0 / OpenID Connect
   - Azure AD / Entra ID
   - Identity Server
   - Auth0, Okta, etc.

2. **Move role assignment to database** instead of hardcoding in claims transformer:
   ```csharp
   var user = await _userRepository.GetByUsernameAsync(username);
   identity.AddClaim(new Claim(SemanticoClaims.Role, user.Role));
   ```

3. **Implement resource-level authorization** for fine-grained control:
   ```csharp
   public async Task<AuthorizationResult?> AuthorizeAsync(
       ResourceType resourceType,
       int resourceId,
       PermissionAction action,
       CancellationToken cancellationToken)
   {
       // Check if user owns the resource
       var resource = await _dbContext.Queries.FindAsync(resourceId);
       if (resource.CreatedByUserId == _userContext.UserId)
           return AuthorizationResult.Success();

       return AuthorizationResult.Failure("Not the owner");
   }
   ```

4. **Add audit logging** to track who does what:
   ```csharp
   await _auditLog.LogAsync(new AuditEntry
   {
       UserId = _userContext.UserId,
       Action = "DeleteQuery",
       ResourceId = queryId,
       Timestamp = DateTime.UtcNow
   });
   ```

## Extending the Example

### Add a New Role

1. Update `SampleClaimsTransformation`:
   ```csharp
   "superadmin" => "SuperAdmin",
   ```

2. Update `SampleAuthorizationProvider`:
   ```csharp
   var role = GetUserRole();
   if (role == SemanticoRole.SuperAdmin)
       return Task.FromResult(true);
   ```

### Integrate with External Auth System

Replace `SampleAuthorizationProvider` with your own:

```csharp
public class ExternalAuthProvider : ISemanticoAuthorizationProvider
{
    private readonly IYourAuthService _authService;
    private readonly ISemanticoUserContext _userContext;

    public async Task<bool> HasWritePermissionAsync(...)
    {
        return await _authService.CheckPermissionAsync(
            _userContext.UserId,
            "semantico.write");
    }
}
```

Register it:
```csharp
options.AddAuthorizationProvider<ExternalAuthProvider>();
```

## Troubleshooting

### Authorization Not Working

1. **Check authorization is enabled:**
   ```csharp
   options.Authorization.Enabled = true;
   ```

2. **Verify claims transformer is registered:**
   ```csharp
   builder.Services.AddScoped<IClaimsTransformation, SampleClaimsTransformation>();
   ```

3. **Check middleware order:**
   ```csharp
   app.UseSemanticoUI()
       .UseBasicAuthentication("admin", "admin")
       .UseAuthorization() // Must be after authentication
       .AddBlazorUI("/semantico");
   ```

### Username Not Displayed

1. Check `ISemanticoUserContext` is registered (done automatically by `AddSemanticoUI()`)
2. Verify claims transformer adds `SemanticoClaims.UserName`

### Permissions Too Restrictive

1. Check role assignment in `SampleClaimsTransformation`
2. Verify `SampleAuthorizationProvider` logic
3. Add logging to see what role is assigned:
   ```csharp
   _logger.LogInformation("User {User} has role {Role}", username, role);
   ```

## Next Steps

- [ ] Replace Basic Auth with real authentication
- [ ] Move role assignment to database
- [ ] Implement resource-level authorization
- [ ] Add audit logging
- [ ] Create migration for audit fields on entities
- [ ] Add UI permission checks (hide/show buttons)
- [ ] Implement permission caching for performance
