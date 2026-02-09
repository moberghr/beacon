---
layout: default
title: User Management
parent: Features
nav_order: 9
---

# User Management

Semantico includes a built-in user management system with support for internal (password-based) users, external (JWT/OAuth) users, and role-based access control.

## Overview

The user management system provides:

- **Internal Users** - Username/password authentication stored in Semantico's database
- **External Users** - JWT/OAuth authentication from your existing identity provider
- **Hybrid Mode** - Support both internal and external users simultaneously
- **Role-Based Access Control** - Admin, Editor, and Viewer roles with level-based permissions
- **First-Run Setup** - Guided wizard to create the initial super admin on first launch
- **User Administration** - Manage users, assign roles, enable/disable accounts via the UI

### Key Features

- Opt-in by default - User management disabled unless explicitly enabled
- Flexible authentication - Internal passwords, external JWT, or hybrid
- Three predefined roles - Admin, Editor, Viewer with clear permission boundaries
- Super admin - Bypass all authorization checks
- Audit trail - Track who assigned roles and when
- Soft delete - Archive users without losing history

## Quick Start

### 1. Enable User Management

Update your `Program.cs`:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";

    // Enable authorization
    options.Authorization.Enabled = true;

    // Enable login form
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

    // Enable user management
    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = true,
        MinimumPasswordLength = 8,
        RequirePasswordComplexity = true
    };
})
.UsePostgreSql(connectionString, "semantico");

builder.Services.AddSemanticoUI();

// Add cookie authentication
builder.Services.AddSemanticoCookieAuthentication("/semantico");

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.UseSemanticoUI()
    .UseLoginForm()
    .UseAuthorization()
    .AddBlazorUI("/semantico");
```

### 2. First-Run Setup

On first launch with no users in the database, Semantico redirects to a setup wizard:

1. Navigate to the application URL
2. You'll be redirected to the setup page automatically
3. Create the initial super admin account (username, email, password)
4. System roles (Admin, Editor, Viewer) are seeded automatically
5. Log in with your new credentials

### 3. Manage Users

After setup, navigate to **Users** in the admin UI to:
- Create new internal users
- Assign roles
- Enable/disable accounts
- View user details and login history

## Roles and Permissions

Semantico includes three predefined system roles:

| Role | Level | Read | Create/Edit | Execute | Delete/Archive |
|------|-------|------|-------------|---------|----------------|
| **Admin** | 3 | Yes | Yes | Yes | Yes |
| **Editor** | 2 | Yes | Yes | Yes | No |
| **Viewer** | 1 | Yes | No | No | No |

### Super Admin

Users with the `IsSuperAdmin` flag bypass all authorization checks. The first user created during setup is automatically a super admin.

### Permission Details

- **Viewer (Level 1+)** - Read-only access to all resources
- **Editor (Level 2+)** - Create, edit, and execute queries, subscriptions, data sources
- **Admin (Level 3)** - Full access including delete, archive, user management, and admin settings

## Authentication Providers

Semantico supports multiple authentication strategies through pluggable providers.

### DatabaseAuthenticationProvider

Authenticates users against Semantico's internal user table with hashed passwords.

```csharp
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
```

Best for: Standalone deployments without an external identity provider.

### JwtExternalApiAuthenticationProvider

Authenticates users via an external JWT/OAuth identity provider.

```csharp
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<JwtExternalApiAuthenticationProvider>();
```

Configure JWT validation in `appsettings.json`:

```json
{
  "Semantico": {
    "Authentication": {
      "Jwt": {
        "ExternalLoginEndpoint": "https://your-idp.com/api/auth/login",
        "EnableBearerAuthentication": true,
        "Validation": {
          "ValidIssuer": "https://your-idp.com",
          "ValidAudience": "semantico",
          "IssuerSigningKey": "your-signing-key"
        },
        "ClaimsMapping": {
          "UserIdClaim": "sub",
          "UserNameClaim": "preferred_username",
          "EmailClaim": "email",
          "RolesClaim": "roles"
        }
      }
    }
  }
}
```

Best for: Organizations with an existing identity provider (Keycloak, Auth0, Azure AD).

### HybridAuthenticationProvider

Tries internal database authentication first, then falls back to external JWT authentication.

```csharp
options.AddAuthenticationProvider<HybridAuthenticationProvider>();
```

Best for: Organizations transitioning from internal to external auth, or supporting both admin and regular users.

## Implementing User Management for Consumers

This section explains how to integrate Semantico's user management into your own application.

### Option 1: Use Built-in User Management (Recommended)

The simplest approach - enable Semantico's built-in user management and let it handle everything:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<SemanticoScheduler>();

    options.Authorization.Enabled = true;
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = true,
        MinimumPasswordLength = 8,
        RequirePasswordComplexity = true
    };
})
.UsePostgreSql(connectionString, "semantico");

builder.Services.AddSemanticoUI();
builder.Services.AddSemanticoCookieAuthentication("/semantico");
```

This gives you:
- Login form at `/semantico/login`
- First-run setup wizard
- User management UI at `/semantico/users`
- Cookie-based sessions (24h default, 30 days with "Remember Me")
- Password hashing with salt

### Option 2: External Identity Provider (JWT/OAuth)

Integrate with your existing identity provider:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.Authentication.EnableLoginForm = true;
    options.AddAuthenticationProvider<JwtExternalApiAuthenticationProvider>();

    options.UserManagement = new UserManagementOptions
    {
        Enabled = true,
        AllowInternalUsers = false  // External users only
    };
})
.UsePostgreSql(connectionString, "semantico");
```

**Pre-register external users** so they get Semantico roles:

```csharp
// In your user provisioning code
var userService = serviceProvider.GetRequiredService<IUserManagementService>();

await userService.CreateUserAsync(new CreateUserRequest
{
    ExternalId = "jwt-sub-claim-value",  // Maps to JWT 'sub' claim
    UserName = "john.doe",
    Email = "john@example.com",
    DisplayName = "John Doe",
    IsInternalUser = false,
    RoleIds = new[] { editorRoleId }
});
```

When an external user authenticates via JWT, the `HybridAuthenticationProvider` looks up their `ExternalId` (from the JWT `sub` claim) and loads their Semantico roles.

### Option 3: Custom Authentication Provider

Build your own authentication logic:

```csharp
public class MyAuthenticationProvider : ISemanticoAuthenticationProvider
{
    private readonly IMyAuthService _authService;

    public MyAuthenticationProvider(IMyAuthService authService)
    {
        _authService = authService;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        // Call your auth system (LDAP, Active Directory, API, etc.)
        var result = await _authService.ValidateCredentialsAsync(username, password);

        if (!result.Success)
            return AuthenticationResult.Failed(result.ErrorMessage);

        return AuthenticationResult.Succeeded(new AuthenticatedUser
        {
            UserId = result.User.Id,
            UserName = result.User.Username,
            Email = result.User.Email,
            DisplayName = result.User.FullName,
            Roles = result.User.Roles
        });
    }

    public Task<bool> ValidateSessionAsync(string userId, CancellationToken ct)
        => Task.FromResult(true);

    public Task SignOutAsync(CancellationToken ct)
        => Task.CompletedTask;
}

// Register it
options.AddAuthenticationProvider<MyAuthenticationProvider>();
```

### Option 4: Custom Authorization Only (No User Management)

If you already handle authentication and just need Semantico to check permissions:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
    // No user management, no login form
})
.UsePostgreSql(connectionString, "semantico");

// Add your own claims transformer
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();

// Use basic auth or your own auth
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");
```

## User Entity Model

The `SemanticoUser` entity stores user information:

| Field | Type | Description |
|-------|------|-------------|
| `ExternalId` | string | GUID for internal users, JWT `sub` for external |
| `UserName` | string | Unique username |
| `Email` | string? | Email address |
| `DisplayName` | string? | Friendly display name |
| `IsInternalUser` | bool | True if password stored in Semantico |
| `PasswordHash` | string? | Hashed password (null for external users) |
| `IsSuperAdmin` | bool | Bypass all authorization checks |
| `IsEnabled` | bool | Account enabled/disabled |
| `LastLoginAt` | DateTime? | Last successful login timestamp |
| `UserRoles` | list | Assigned roles with audit info |

## Cookie Authentication Options

Configure session behavior:

```csharp
builder.Services.AddSemanticoCookieAuthentication("/semantico", options =>
{
    options.CookieExpirationHours = 24;       // Normal session duration
    options.RememberMeExpirationDays = 30;    // "Remember Me" duration
});
```

## Database Schema

User management creates these tables in your Semantico schema:

- **`users`** - User accounts (internal and external)
- **`roles`** - System roles (Admin, Editor, Viewer)
- **`user_roles`** - Many-to-many join with audit fields (assigned_by, assigned_at)

These tables are created automatically by the EF Core migration `20260206112218_UserManagement`.

## API Endpoints

### Authentication

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/auth/login` | POST | Authenticate with username/password |
| `/api/auth/logout` | POST | Clear session cookie |
| `/api/auth/signout` | GET | Browser-navigable sign out with redirect |

### First-Run Setup

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/setup/status` | GET | Check if first-run setup is needed |
| `/api/setup/superadmin` | POST | Create super admin (first run only) |
| `/api/setup/roles` | GET | List available roles |

## Troubleshooting

### Login Page Not Showing

**Problem:** Navigating to the app doesn't show a login form.

**Solution:**
1. Ensure `EnableLoginForm = true` in authentication options
2. Ensure `.UseLoginForm()` is called in the middleware pipeline
3. Check that `AddSemanticoCookieAuthentication()` is registered

### External Users Can't Log In

**Problem:** JWT users get "unauthorized" errors.

**Solution:**
1. Pre-register the user with their `ExternalId` matching the JWT `sub` claim
2. Verify JWT validation settings (issuer, audience, signing key)
3. Check claims mapping matches your JWT token structure

### Roles Not Applied

**Problem:** Users are authenticated but permissions don't work.

**Solution:**
1. Verify the user has roles assigned in the Users management page
2. Check that `DatabaseAuthorizationProvider` is registered (not just `RoleBasedAuthorizationProvider`)
3. Ensure authorization is enabled: `options.Authorization.Enabled = true`

### First-Run Setup Doesn't Appear

**Problem:** App shows login form instead of setup wizard.

**Solution:** The setup wizard only appears when no users exist in the database. If you've already created users manually, the setup is skipped.

## See Also

- [Authorization Guide](authorization) - Permission system details
- [Admin Settings](admin-settings) - Runtime configuration
- [Configuration Guide](../getting-started/configuration) - Full configuration reference
