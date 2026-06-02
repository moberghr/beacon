---
layout: default
title: Configuration
parent: Getting Started
nav_order: 3
---

# Configuration Guide

Complete reference for all Beacon configuration options.

## Configuration in Program.cs

Beacon is configured in your ASP.NET Core application's `Program.cs` file using a single method call.

### Basic Configuration

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.UI;

// Step 1: Add core services and configure database provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
        options.BaseUrl = "https://your-domain.com/beacon"; // For notification links
        options.UseAI = true; // Enable AI features (optional)
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
// Or use SQL Server:
// .UseSqlServer(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

// Step 2: Add UI components
builder.Services.AddBeaconUI();

// Step 3: Add AI services (optional)
builder.Services.AddBeaconAI(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles(); // Required: Serves Beacon UI assets

// Configure UI
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/beacon");
```

## Base URL Configuration

The `BaseUrl` setting specifies where your Beacon admin UI is hosted. This URL is used to generate clickable links in notifications (especially Teams messages) that take users directly to notification details.

### Setting Base URL

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
        // Set the URL where Beacon UI is accessible
        options.BaseUrl = "https://your-domain.com/beacon";
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
```

### Base URL Examples

**Development (localhost):**
```csharp
options.BaseUrl = "https://localhost:7187/beacon";
```

**Staging:**
```csharp
options.BaseUrl = "https://staging.yourdomain.com/beacon";
```

**Production:**
```csharp
options.BaseUrl = "https://yourdomain.com/beacon";
```

**From configuration:**
```csharp
options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];
```

### appsettings.json Configuration

```json
{
  "Beacon": {
    "BaseUrl": "https://yourdomain.com/beacon",
    "EncryptionKey": "your-secure-32-character-key-here"
  }
}
```

## Encryption Key Configuration ⚠️ REQUIRED

The `EncryptionKey` is **required** for encrypting sensitive data like database connection strings.

### Generate a Secure Key

```bash
openssl rand -base64 32
```

This generates a cryptographically secure 32-character random key suitable for AES-256 encryption.

### Configuration

```json
{
  "Beacon": {
    "EncryptionKey": "k8J3m9Lp2Nq5Rt8Vw1Yz4Bc7Df0Gh3Jk6=="
  }
}
```

### Security Best Practices

⚠️ **Never commit encryption keys to source control**

**Development:**
```json
{
  "Beacon": {
    "EncryptionKey": "DevKey_OnlyForLocalDevelopment_ChangeInProd!"
  }
}
```

**Production - Use Environment Variables:**
```json
{
  "Beacon": {
    "EncryptionKey": "${BEACON_ENCRYPTION_KEY}"
  }
}
```

Then set the environment variable:
```bash
export BEACON_ENCRYPTION_KEY="your-production-key-here"
```

**Production - Use Azure Key Vault:**
```csharp
var keyVaultEndpoint = new Uri(Environment.GetEnvironmentVariable("VaultUri"));
var secretClient = new SecretClient(keyVaultEndpoint, new DefaultAzureCredential());
var secret = await secretClient.GetSecretAsync("BeaconEncryptionKey");
builder.Configuration["Beacon:EncryptionKey"] = secret.Value.Value;
```

### What Gets Encrypted

The encryption key is used to encrypt:
- Data source connection strings (stored in database)
- Other sensitive configuration values (as needed)

### Error if Missing

If the encryption key is not configured, Beacon will throw an exception on startup:

```
InvalidOperationException: Beacon:EncryptionKey must be configured.
Generate a secure key with: openssl rand -base64 32
Then add to appsettings.json: { "Beacon": { "EncryptionKey": "your-generated-key" } }
```

## Authorization Configuration (Optional)

Beacon provides a flexible authorization system for controlling user access. Authorization is **disabled by default** (backward compatible).

### Quick Start - Enable Authorization

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/beacon";

    // Enable authorization with role-based access control
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

builder.Services.AddBeaconUI();

// Enable authorization middleware
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // ← Add this line
    .AddBlazorUI("/beacon");
```

### Built-in Authorization Providers

**DefaultAuthorizationProvider** (default):
- Allows all operations
- Used when authorization is disabled
- No configuration required

**RoleBasedAuthorizationProvider**:
- Simple RBAC with Admin, Editor, Viewer roles
- Requires role claims to be added after authentication

### Add Role Claims

When using `RoleBasedAuthorizationProvider`, add a claims transformer:

```csharp
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;

public class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add role based on your logic
        identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        identity.AddClaim(new Claim(BeaconClaims.UserId, principal.Identity.Name));
        identity.AddClaim(new Claim(BeaconClaims.UserName, principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

// Register it
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();
```

### Authorization Options

```csharp
options.Authorization.Enabled = true; // Enable authorization
options.Authorization.EnableResourceLevelAuthorization = true; // Enable fine-grained permissions
options.AddAuthorizationProvider<MyCustomProvider>(); // Use custom provider
```

### Custom Authorization Provider

Create your own authorization logic:

```csharp
public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public MyAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
    {
        // Your logic here
        return Task.FromResult(_userContext.IsAuthenticated);
    }

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
    {
        // Your logic here
        return Task.FromResult(_userContext.HasClaim(BeaconClaims.Role, "Admin"));
    }

    // Implement other methods...
}
```

{: .note }
> For complete authorization documentation, integration examples, and advanced scenarios, see the [Authorization Guide](../features/authorization.md).

## AI/LLM Configuration (Optional - Experimental)

{: .warning }
> **Experimental Feature:** AI-powered features are experimental and may produce incorrect results. Configure only if you understand the limitations and will validate all AI-generated content.

Configure LLM providers for AI-powered features like documentation generation and natural language alert creation.

{: .note }
> LLM configuration can also be managed at runtime via the [Admin Settings](../features/admin-settings) UI. Settings saved there take precedence over `appsettings.json` values and support hot-swapping providers without restarting.

### Supported Providers

- **OpenAI**: gpt-4o (recommended), gpt-4
- **Anthropic Claude**: claude-3-5-sonnet-20241022 (recommended)
- **Azure OpenAI**: Latest models only

{: .note }
> Use latest models only. Older models (gpt-3.5-turbo, claude-3-opus) are not recommended.

### Basic Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o"
    }
  }
}
```

### Complete Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o",
      "BaseUrl": "https://api.openai.com/v1",
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

### OpenAI Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-...",
      "Model": "gpt-4o",
      "BaseUrl": "https://api.openai.com/v1"
    }
  }
}
```

**Recommended model:**
- `gpt-4o` - Best balance of cost and performance (use this)

**Alternative:**
- `gpt-4` - Higher quality, more expensive

### Anthropic Claude Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Anthropic",
      "ApiKey": "sk-ant-...",
      "Model": "claude-3-5-sonnet-20241022",
      "BaseUrl": "https://api.anthropic.com"
    }
  }
}
```

**Recommended model:**
- `claude-3-5-sonnet-20241022` - Best overall (use this)

### Azure OpenAI Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "AzureOpenAI",
      "ApiKey": "your-azure-key",
      "Model": "your-deployment-name",
      "BaseUrl": "https://your-resource.openai.azure.com"
    }
  }
}
```

### Rate Limiting

Control API usage and costs with rate limits:

```json
{
  "Beacon": {
    "LLM": {
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

| Setting | Description | Default | Recommended |
|---------|-------------|---------|-------------|
| `MaxConcurrentRequests` | Max parallel AI requests | 5 | 3-10 based on tier |
| `RequestsPerMinute` | Rate limit per minute | 60 | Match provider tier |
| `MaxTokensPerRequest` | Max tokens per request | 4000 | 4000-8000 |

### Cost Estimation

Typical costs per operation:

| Operation | Tokens | OpenAI (gpt-4o) | Anthropic (Claude 3.5) |
|-----------|--------|-----------------|------------------------|
| Small doc generation (10 tables) | ~2,500 | $0.02 | $0.01 |
| Large doc generation (50 tables) | ~8,000 | $0.06 | $0.04 |
| Alert query generation | ~1,000 | $0.01 | $0.01 |

### Environment-Specific Configuration

**Development:**
```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-dev-key-here",
      "Model": "gpt-4o"  // Use same model as production
    }
  }
}
```

**Production - Use Environment Variables:**
```json
{
  "Beacon": {
    "LLM": {
      "ApiKey": "${LLM_API_KEY}"
    }
  }
}
```

### How It's Used

When `BaseUrl` is configured, Teams notifications include a **"View Query Results"** button that links to:
```
{BaseUrl}/notifications/details/{notificationId}
```

For example:
```
https://yourdomain.com/beacon/notifications/details/12345
```

This allows recipients to click through from Teams to view:
- Complete query results
- Execution metrics
- Query execution history
- All notification details

{: .note }
> If `BaseUrl` is not configured, Teams notifications will still be sent but without the clickable link to view details in the Beacon UI.

## Database Provider Configuration

Database providers are configured within the `AddBeacon` options.

### PostgreSQL Provider

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon"); // Optional schema, defaults to "beacon"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  }
}
```

### SQL Server Provider

```csharp
using Beacon.Core;
using Beacon.Core.SqlServer;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UseSqlServer(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon"); // Optional schema, defaults to "beacon"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Connection String Reference

### PostgreSQL Connection Strings

**Basic connection:**
```
Host=hostname;Port=5432;Database=dbname;Username=user;Password=pass
```

**With SSL:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;SSL Mode=Require
```

**With connection pooling:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;Pooling=true;MinPoolSize=0;MaxPoolSize=100
```

**Connection timeout:**
```
Host=hostname;Database=dbname;Username=user;Password=pass;Timeout=30;CommandTimeout=60
```

**Parameters:**
- `Host`: PostgreSQL server hostname or IP
- `Port`: Port number (default: 5432)
- `Database`: Database name
- `Username`: Database user
- `Password`: User password
- `SSL Mode`: None, Prefer, Require, VerifyCA, VerifyFull
- `Pooling`: Enable connection pooling (recommended: true)
- `MinPoolSize`: Minimum connections in pool
- `MaxPoolSize`: Maximum connections in pool
- `Timeout`: Connection timeout in seconds
- `CommandTimeout`: Command execution timeout in seconds

### SQL Server Connection Strings

**Basic connection:**
```
Server=hostname;Database=dbname;User Id=user;Password=pass;TrustServerCertificate=True
```

**Windows Authentication:**
```
Server=hostname;Database=dbname;Integrated Security=True
```

**With encryption:**
```
Server=hostname;Database=dbname;User Id=user;Password=pass;Encrypt=True;TrustServerCertificate=False
```

**Parameters:**
- `Server`: SQL Server hostname or IP
- `Database`: Database name
- `User Id`: SQL Server login
- `Password`: Login password
- `Integrated Security`: Use Windows authentication
- `TrustServerCertificate`: Trust server certificate (True for self-signed)
- `Encrypt`: Encrypt connection
- `Connection Timeout`: Connection timeout in seconds

### MySQL Connection Strings (for query projects)

**Basic connection:**
```
Server=hostname;Port=3306;Database=dbname;Uid=user;Pwd=pass
```

**With SSL:**
```
Server=hostname;Database=dbname;Uid=user;Pwd=pass;SslMode=Required
```

## Scheduler Configuration

Beacon requires an `IBeaconScheduler` implementation for job scheduling. You can use any job scheduler library (Hangfire, Quartz.NET, etc.) or create a custom implementation.

### Example: Hangfire Scheduler Implementation

```csharp
using Hangfire;
using Beacon.Core.Worker;

public class BeaconScheduler : IBeaconScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public BeaconScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.AddOrUpdate<IJobService>(
            jobKey,
            x => x.ExecuteQuery(subscriptionId),
            cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.RemoveIfExists(jobKey);
    }
}
```

Register the scheduler:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
```

## UI Configuration

### Basic Authentication (Simple)

For quick setups without user management:

```csharp
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "secretpassword")
    .AddBlazorUI("/beacon");
```

### Login Form Authentication (Recommended)

For production deployments with user management:

```csharp
builder.Services.AddBeaconCookieAuthentication("/beacon");

app.UseAuthentication();
app.UseAuthorization();

app.UseBeaconUI()
    .UseLoginForm()
    .UseAuthorization()
    .AddBlazorUI("/beacon");
```

### Custom UI Path

Change the UI path from default `/beacon`:

```csharp
app.UseBeaconUI()
    .UseLoginForm()
    .AddBlazorUI("/alerts"); // Custom path
```

Access UI at: `http://localhost:5000/alerts`

## User Management Configuration (Optional)

Enable built-in user management with login form, role-based access, and first-run setup.

### Quick Start

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://your-domain.com/beacon";

    // Enable authorization + user management
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
.UsePostgreSql(connectionString, "beacon");

builder.Services.AddBeaconUI();
builder.Services.AddBeaconCookieAuthentication("/beacon");

app.UseAuthentication();
app.UseAuthorization();

app.UseBeaconUI()
    .UseLoginForm()
    .UseAuthorization()
    .AddBlazorUI("/beacon");
```

### User Management Options

| Option | Description | Default |
|--------|-------------|---------|
| `Enabled` | Enable user management features | `false` |
| `AllowInternalUsers` | Allow password-based users | `true` |
| `MinimumPasswordLength` | Minimum password length | `8` |
| `RequirePasswordComplexity` | Require mixed case, numbers, symbols | `true` |

### Authentication Providers

| Provider | Use Case |
|----------|----------|
| `DatabaseAuthenticationProvider` | Internal users with passwords in Beacon |
| `JwtExternalApiAuthenticationProvider` | External JWT/OAuth identity provider |
| `HybridAuthenticationProvider` | Both internal and external users |

{: .note }
> For complete user management documentation including external JWT setup, custom providers, and role details, see the [User Management Guide](../features/user-management).

### Custom Authorization Provider

Implement custom authorization logic:

```csharp
public class CustomAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public CustomAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken ct = default)
        => Task.FromResult(_userContext.IsAuthenticated);

    public Task<bool> HasWritePermissionAsync(CancellationToken ct = default)
        => Task.FromResult(_userContext.HasClaim(BeaconClaims.Role, "Admin"));

    // Implement other methods...
}

// Register it
options.AddAuthorizationProvider<CustomAuthorizationProvider>();
```

## Email Adapter

### Custom Email Implementation

Implement `IEmailAdapter` for your email provider:

```csharp
using Beacon.Core.Adapters.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly SmtpClient _smtpClient;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _smtpClient = new SmtpClient
        {
            Host = configuration["Smtp:Host"],
            Port = int.Parse(configuration["Smtp:Port"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                configuration["Smtp:Username"],
                configuration["Smtp:Password"])
        };
    }

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
    {
        var message = new MailMessage
        {
            From = new MailAddress("alerts@yourdomain.com", "Beacon Alerts"),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await _smtpClient.SendMailAsync(message);
    }
}
```

Register the adapter:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
```

Add SMTP settings to `appsettings.json`:

```json
{
  "Smtp": {
    "Host": "smtp.gmail.com",
    "Port": "587",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password"
  }
}
```

## Schema Configuration

### Custom Schema Name

Specify a custom schema for multi-tenant deployments:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "tenant_acme"); // Custom schema name
```

### Runtime Schema Selection

Select schema based on configuration or tenant context:

```csharp
var schema = builder.Configuration["Beacon:Schema"] ?? "beacon";

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        schema);
```

**In appsettings.json:**
```json
{
  "Beacon": {
    "Schema": "production_beacon"
  }
}
```

### Multi-Tenant Deployment

Each tenant gets its own schema in the same database:

```csharp
// Detect tenant from request context
var tenantId = GetTenantFromContext();
var schema = $"tenant_{tenantId}";

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        schema);
```

📚 [Learn more about multi-tenant deployments →](../advanced/multi-tenant)

## Job Scheduler Configuration

Beacon works with any job scheduler that implements `IBeaconScheduler`. The examples below use Hangfire, but you can adapt them to Quartz.NET or other schedulers.

### Hangfire Storage Provider (Example)

**PostgreSQL (recommended):**
```csharp
using Hangfire.PostgreSql;

builder.Services.AddHangfire(config => config
    .UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
    {
        PrepareSchemaIfNecessary = true
    }));
```

**SQL Server:**
```csharp
using Hangfire.SqlServer;

builder.Services.AddHangfire(config => config
    .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
    {
        PrepareSchemaIfNecessary = true
    }));
```

**Redis:**
```csharp
using Hangfire.Redis;

builder.Services.AddHangfire(config => config
    .UseRedisStorage(connectionString));
```

### Worker Count

Adjust worker count based on subscription load:

```csharp
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10; // Default: CPU core count
});
```

**Guidelines:**
- Light load (< 100 subscriptions): 2-4 workers
- Medium load (100-500 subscriptions): 5-10 workers
- Heavy load (500+ subscriptions): 10-20 workers

## Security Configuration

### Database User Permissions

**For Beacon metadata database (PostgreSQL):**
```sql
CREATE USER beacon WITH PASSWORD 'strong-password';
GRANT ALL PRIVILEGES ON DATABASE beacon TO beacon;
GRANT ALL ON SCHEMA beacon TO beacon;
```

**For query projects (read-only recommended):**
```sql
CREATE USER beacon_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO beacon_readonly;
GRANT USAGE ON SCHEMA public TO beacon_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO beacon_readonly;
```

### Connection String Security

**Best practices:**
- ✓ Use User Secrets for development
- ✓ Use Azure Key Vault or similar for production
- ✓ Use read-only users for query projects
- ✓ Enable SSL/TLS for production
- ✓ Rotate passwords regularly

**User Secrets (development):**
```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=postgres;Password=devpassword"
```

**Azure Key Vault (production):**
```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{keyVaultName}.vault.azure.net/"),
    new DefaultAzureCredential());
```

## Performance Configuration

### Connection Pooling

Enable connection pooling for better performance:

**PostgreSQL:**
```
Host=postgres;Database=beacon;Username=sa;Password=pass;Pooling=true;MinPoolSize=5;MaxPoolSize=50
```

**SQL Server:**
```
Server=sqlserver;Database=beacon;User Id=sa;Password=pass;Max Pool Size=50;Min Pool Size=5
```

### Query Timeout

Set appropriate timeouts for long-running queries:

**In connection string:**
```
Host=postgres;Database=beacon;Username=sa;Password=pass;CommandTimeout=300
```

**In subscription settings:**
- Set timeout to match expected query duration
- Default: 60 seconds
- Long-running reports: 300-600 seconds

## Notification Configuration

### Email Adapter

Beacon doesn't include a default email implementation. You must provide your own:

```csharp
using Beacon.Core.Adapters.Mail;
using System.Net.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly IConfiguration _configuration;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
    {
        using var smtpClient = new SmtpClient
        {
            Host = _configuration["Email:SmtpHost"],
            Port = int.Parse(_configuration["Email:SmtpPort"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                _configuration["Email:Username"],
                _configuration["Email:Password"])
        };

        var message = new MailMessage
        {
            From = new MailAddress(
                _configuration["Email:FromAddress"],
                _configuration["Email:FromName"]),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await smtpClient.SendMailAsync(message);
    }
}
```

Register the adapter:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
```

**Email configuration in appsettings.json:**
```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "your-email@gmail.com",
    "Password": "your-app-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  }
}
```

### Teams Notifications

Teams notifications work out-of-the-box. No additional configuration needed beyond creating recipient with webhook URL.

### Jira Notifications

Jira notifications work out-of-the-box. Configure per recipient with Jira instance details.

## Schema Configuration

### Default Schema

By default, Beacon uses `"beacon"` schema:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!);
    // Uses "beacon" schema by default
```

### Custom Schema

Specify custom schema for multi-tenancy or environment separation:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "custom_schema");
```

### Runtime Schema Selection

Load schema from configuration:

```csharp
var schema = builder.Configuration["Beacon:Schema"] ?? "beacon";

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        schema);
```

**In appsettings.json:**
```json
{
  "Beacon": {
    "Schema": "production_beacon"
  }
}
```

**Environment-specific schemas:**

**appsettings.Development.json:**
```json
{
  "Beacon": {
    "Schema": "dev_beacon"
  }
}
```

**appsettings.Production.json:**
```json
{
  "Beacon": {
    "Schema": "prod_beacon"
  }
}
```

## UI Path Configuration

### Custom Base Path

Change the Beacon UI path:

```csharp
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/custom-path");
```

Access UI at: `http://localhost:5000/custom-path`

### Multiple Paths

Host Beacon UI at multiple paths (for different auth):

```csharp
app.UseBeaconUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/beacon");

app.UseBeaconUI()
    .UseBasicAuthentication("viewer", "readonly")
    .AddBlazorUI("/beacon-viewer");
```

## Logging Configuration

Configure logging in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Debug",
      "Hangfire": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

**Log levels:**
- `Trace`: Detailed diagnostic information
- `Debug`: Debugging information
- `Information`: General informational messages
- `Warning`: Warnings that might need attention
- `Error`: Errors and exceptions
- `Critical`: Critical failures

## Complete Configuration Example

### With User Management (Recommended)

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.PostgreSql;
using Beacon.UI;

var builder = WebApplication.CreateBuilder(args);

// Hangfire configuration
builder.Services.AddHangfire((provider, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("BeaconContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10;
});

// Step 1: Add Beacon core services with user management
var schema = builder.Configuration["Beacon:Schema"] ?? "beacon";
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];
        options.UseAI = true;

        // Enable authorization and user management
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
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        schema);

// Step 2: Add UI components
builder.Services.AddBeaconUI();

// Step 3: Add cookie authentication
builder.Services.AddBeaconCookieAuthentication("/beacon");

// Step 4: Add AI services (optional)
builder.Services.AddBeaconAI(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// Beacon UI with login form
app.UseBeaconUI()
    .UseLoginForm()
    .UseAuthorization()
    .AddBlazorUI("/beacon");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire");

app.Run();
```

### Without User Management (Simple)

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];
    })
    .UsePostgreSql(connectionString, schema);

builder.Services.AddBeaconUI();

app.UseBeaconUI()
    .UseBasicAuthentication("admin", "secretpassword")
    .AddBlazorUI("/beacon");
```

**Complete appsettings.json:**
```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=beacon;Password=secretpass;Pooling=true;MaxPoolSize=50"
  },
  "Beacon": {
    "EncryptionKey": "your-secure-32-character-key-here",
    "Schema": "beacon",
    "BaseUrl": "https://yourdomain.com/beacon",
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key",
      "Model": "gpt-4o"
    }
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "app-specific-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Information",
      "Hangfire": "Warning"
    }
  }
}
```

{: .note }
> LLM settings in `appsettings.json` are optional startup defaults. Once configured via [Admin Settings](../features/admin-settings), database values take precedence.

## Next Steps

<div class="code-example" markdown="1">
📊 **[Features →](../features/)**

Explore Projects, Queries, Subscriptions, and more
</div>

<div class="code-example" markdown="1">
🔧 **[Troubleshooting →](../troubleshooting/common-issues)**

Solutions for common configuration problems
</div>

<div class="code-example" markdown="1">
🏗️ **[Architecture →](../advanced/architecture)**

Understand Beacon's extensibility points
</div>
