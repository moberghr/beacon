# Semantico.UI.AspNet

ASP.NET Core integration for Semantico UI.

## Overview

This package provides middleware and service extensions for hosting the Semantico Blazor admin interface in ASP.NET Core applications with built-in authentication and authorization support.

## Installation

```bash
dotnet add package Semantico.Core.PostgreSql
dotnet add package Semantico.UI.AspNet
```

## Quick Start

### 1. Configure in Program.cs

```csharp
using Semantico.Core.PostgreSql;
using Semantico.UI.AspNet;

var builder = WebApplication.CreateBuilder(args);

// Configure database provider
builder.Services.AddPostgreSqlSemantico(
    builder.Configuration.GetConnectionString("SemanticoContext")!,
    schema: "semantico");

// Configure Semantico admin
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";
});

var app = builder.Build();

// Add Semantico UI with authentication
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");

// Run migrations
ServiceConfiguration.UseSemantico(app.Services);

app.Run();
```

### 2. Add Connection String

In `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  }
}
```

### 3. Access the UI

Navigate to: `http://localhost:5000/semantico`

Login with the credentials configured in `UseBasicAuthentication`.

## Features

- Blazor Server UI hosting
- Basic authentication middleware
- Custom authorization provider support
- Configurable UI path
- Automatic service registration

## Authentication

### Basic Authentication

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "secretpassword")
    .AddBlazorUI("/semantico");
```

### Custom Authorization Provider

Implement `ISemanticoAuthorizationProvider` for fine-grained permissions:

```csharp
public class CustomAuthProvider : ISemanticoAuthorizationProvider
{
    public Task<bool> HasReadPermissionAsync(string username)
    {
        // Your read permission logic
    }

    public Task<bool> HasWritePermissionAsync(string username)
    {
        // Your write permission logic
    }
}
```

Register the provider:

```csharp
builder.Services.AddSemanticoAdmin(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.AddAuthorizationProvider<CustomAuthProvider>();
});

app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/semantico");
```

## Configuration Options

- **BaseUrl**: Set the base URL for notification links
- **Custom UI Path**: Change from `/semantico` to any path
- **Authorization Provider**: Implement custom permission logic
- **Email Adapter**: Configure email notifications

## Documentation

- [Full Documentation](https://moberghr.github.io/semantico)
- [Getting Started](https://moberghr.github.io/semantico/getting-started/quick-start)
- [Configuration Guide](https://moberghr.github.io/semantico/getting-started/configuration)

## License

MIT License - see [LICENSE](https://github.com/moberghr/semantico/blob/main/LICENSE)
