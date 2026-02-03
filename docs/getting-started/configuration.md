---
layout: default
title: Configuration
parent: Getting Started
nav_order: 3
---

# Configuration Guide

Complete reference for all Semantico configuration options.

## Configuration in Program.cs

Semantico is configured in your ASP.NET Core application's `Program.cs` file using a single method call.

### Basic Configuration

```csharp
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.UI;

// Step 1: Add core services and configure database provider
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
        options.BaseUrl = "https://your-domain.com/semantico"; // For notification links
        options.UseAI = true; // Enable AI features (optional)
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
// Or use SQL Server:
// .UseSqlServer(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");

// Step 2: Add UI components
builder.Services.AddSemanticoUI();

// Step 3: Add AI services (optional)
builder.Services.AddSemanticoAI(builder.Configuration);

var app = builder.Build();

app.UseStaticFiles(); // Required: Serves Semantico UI assets

// Configure UI
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");
```

## Base URL Configuration

The `BaseUrl` setting specifies where your Semantico admin UI is hosted. This URL is used to generate clickable links in notifications (especially Teams messages) that take users directly to notification details.

### Setting Base URL

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
        // Set the URL where Semantico UI is accessible
        options.BaseUrl = "https://your-domain.com/semantico";
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
```

### Base URL Examples

**Development (localhost):**
```csharp
options.BaseUrl = "https://localhost:7187/semantico";
```

**Staging:**
```csharp
options.BaseUrl = "https://staging.yourdomain.com/semantico";
```

**Production:**
```csharp
options.BaseUrl = "https://yourdomain.com/semantico";
```

**From configuration:**
```csharp
options.BaseUrl = builder.Configuration["Semantico:BaseUrl"];
```

### appsettings.json Configuration

```json
{
  "Semantico": {
    "BaseUrl": "https://yourdomain.com/semantico",
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
  "Semantico": {
    "EncryptionKey": "k8J3m9Lp2Nq5Rt8Vw1Yz4Bc7Df0Gh3Jk6=="
  }
}
```

### Security Best Practices

⚠️ **Never commit encryption keys to source control**

**Development:**
```json
{
  "Semantico": {
    "EncryptionKey": "DevKey_OnlyForLocalDevelopment_ChangeInProd!"
  }
}
```

**Production - Use Environment Variables:**
```json
{
  "Semantico": {
    "EncryptionKey": "${SEMANTICO_ENCRYPTION_KEY}"
  }
}
```

Then set the environment variable:
```bash
export SEMANTICO_ENCRYPTION_KEY="your-production-key-here"
```

**Production - Use Azure Key Vault:**
```csharp
var keyVaultEndpoint = new Uri(Environment.GetEnvironmentVariable("VaultUri"));
var secretClient = new SecretClient(keyVaultEndpoint, new DefaultAzureCredential());
var secret = await secretClient.GetSecretAsync("SemanticoEncryptionKey");
builder.Configuration["Semantico:EncryptionKey"] = secret.Value.Value;
```

### What Gets Encrypted

The encryption key is used to encrypt:
- Data source connection strings (stored in database)
- Other sensitive configuration values (as needed)

### Error if Missing

If the encryption key is not configured, Semantico will throw an exception on startup:

```
InvalidOperationException: Semantico:EncryptionKey must be configured.
Generate a secure key with: openssl rand -base64 32
Then add to appsettings.json: { "Semantico": { "EncryptionKey": "your-generated-key" } }
```

## Authorization Configuration (Optional)

Semantico provides a flexible authorization system for controlling user access. Authorization is **disabled by default** (backward compatible).

### Quick Start - Enable Authorization

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
{
    options.AddSemanticoScheduler<YourScheduler>();
    options.BaseUrl = "https://your-domain.com/semantico";

    // Enable authorization with role-based access control
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<RoleBasedAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "semantico");

builder.Services.AddSemanticoUI();

// Enable authorization middleware
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // ← Add this line
    .AddBlazorUI("/semantico");
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
using Semantico.Core.Authorization;

public class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add role based on your logic
        identity.AddClaim(new Claim(SemanticoClaims.Role, "Admin"));
        identity.AddClaim(new Claim(SemanticoClaims.UserId, principal.Identity.Name));
        identity.AddClaim(new Claim(SemanticoClaims.UserName, principal.Identity.Name));

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
public class MyAuthorizationProvider : ISemanticoAuthorizationProvider
{
    private readonly ISemanticoUserContext _userContext;

    public MyAuthorizationProvider(ISemanticoUserContext userContext)
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
        return Task.FromResult(_userContext.HasClaim(SemanticoClaims.Role, "Admin"));
    }

    // Implement other methods...
}
```

{: .note }
> For complete authorization documentation, integration examples, and advanced scenarios, see the [Authorization Guide](../features/authorization.md).

## AI/LLM Configuration (Optional - Experimental)

{: .warning }
> **⚠️ Experimental Feature:** AI-powered features are experimental and may produce incorrect results. Configure only if you understand the limitations and will validate all AI-generated content.

Configure LLM providers for AI-powered features like documentation generation and natural language alert creation.

### Supported Providers

- **OpenAI**: gpt-4o (recommended), gpt-4
- **Anthropic Claude**: claude-3-5-sonnet-20241022 (recommended)
- **Azure OpenAI**: Latest models only

{: .note }
> Use latest models only. Older models (gpt-3.5-turbo, claude-3-opus) are not recommended.

### Basic Configuration

```json
{
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
  "Semantico": {
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
https://yourdomain.com/semantico/notifications/details/12345
```

This allows recipients to click through from Teams to view:
- Complete query results
- Execution metrics
- Query execution history
- All notification details

{: .note }
> If `BaseUrl` is not configured, Teams notifications will still be sent but without the clickable link to view details in the Semantico UI.

## Database Provider Configuration

Database providers are configured within the `AddSemantico` options.

### PostgreSQL Provider

```csharp
using Semantico.Core;
using Semantico.Core.PostgreSql;

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        "semantico"); // Optional schema, defaults to "semantico"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=postgres;Password=yourpassword"
  }
}
```

### SQL Server Provider

```csharp
using Semantico.Core;
using Semantico.Core.SqlServer;

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UseSqlServer(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        "semantico"); // Optional schema, defaults to "semantico"
```

**Connection string in appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Server=localhost;Database=semantico;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
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

Semantico requires an `ISemanticoScheduler` implementation for job scheduling. You can use any job scheduler library (Hangfire, Quartz.NET, etc.) or create a custom implementation.

### Example: Hangfire Scheduler Implementation

```csharp
using Hangfire;
using Semantico.Core.Worker;

public class SemanticoScheduler : ISemanticoScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public SemanticoScheduler(IRecurringJobManager recurringJobManager)
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
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
```

## UI Configuration

### Basic Authentication

Configure username and password for UI access:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "secretpassword")
    .AddBlazorUI("/semantico");
```

### Custom UI Path

Change the UI path from default `/semantico`:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/alerts"); // Custom path
```

Access UI at: `http://localhost:5000/alerts`

### Authorization Provider

Implement custom authorization logic:

```csharp
using Semantico.UI.AspNet.Authentication;

public class CustomAuthorizationProvider : ISemanticoAuthorizationProvider
{
    private readonly IUserService _userService;

    public CustomAuthorizationProvider(IUserService userService)
    {
        _userService = userService;
    }

    public async Task<bool> HasReadPermissionAsync(string username)
    {
        var user = await _userService.GetUserAsync(username);
        return user?.HasRole("Viewer") ?? false;
    }

    public async Task<bool> HasWritePermissionAsync(string username)
    {
        var user = await _userService.GetUserAsync(username);
        return user?.HasRole("Admin") ?? false;
    }
}
```

Register the provider:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");

builder.Services.AddSemanticoUI(options =>
{
    options.AddAuthorizationProvider<CustomAuthorizationProvider>();
});

// Enable authorization checks
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/semantico");
```

## Email Adapter

### Custom Email Implementation

Implement `IEmailAdapter` for your email provider:

```csharp
using Semantico.Core.Adapters.Mail;

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
            From = new MailAddress("alerts@yourdomain.com", "Semantico Alerts"),
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
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
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
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        "tenant_acme"); // Custom schema name
```

### Runtime Schema Selection

Select schema based on configuration or tenant context:

```csharp
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        schema);
```

**In appsettings.json:**
```json
{
  "Semantico": {
    "Schema": "production_semantico"
  }
}
```

### Multi-Tenant Deployment

Each tenant gets its own schema in the same database:

```csharp
// Detect tenant from request context
var tenantId = GetTenantFromContext();
var schema = $"tenant_{tenantId}";

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        schema);
```

📚 [Learn more about multi-tenant deployments →](../advanced/multi-tenant)

## Job Scheduler Configuration

Semantico works with any job scheduler that implements `ISemanticoScheduler`. The examples below use Hangfire, but you can adapt them to Quartz.NET or other schedulers.

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

**For Semantico metadata database (PostgreSQL):**
```sql
CREATE USER semantico WITH PASSWORD 'strong-password';
GRANT ALL PRIVILEGES ON DATABASE semantico TO semantico;
GRANT ALL ON SCHEMA semantico TO semantico;
```

**For query projects (read-only recommended):**
```sql
CREATE USER semantico_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO semantico_readonly;
GRANT USAGE ON SCHEMA public TO semantico_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO semantico_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO semantico_readonly;
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
dotnet user-secrets set "ConnectionStrings:SemanticoContext" "Host=localhost;Database=semantico;Username=postgres;Password=devpassword"
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
Host=postgres;Database=semantico;Username=sa;Password=pass;Pooling=true;MinPoolSize=5;MaxPoolSize=50
```

**SQL Server:**
```
Server=sqlserver;Database=semantico;User Id=sa;Password=pass;Max Pool Size=50;Min Pool Size=5
```

### Query Timeout

Set appropriate timeouts for long-running queries:

**In connection string:**
```
Host=postgres;Database=semantico;Username=sa;Password=pass;CommandTimeout=300
```

**In subscription settings:**
- Set timeout to match expected query duration
- Default: 60 seconds
- Long-running reports: 300-600 seconds

## Notification Configuration

### Email Adapter

Semantico doesn't include a default email implementation. You must provide your own:

```csharp
using Semantico.Core.Adapters.Mail;
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
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!, "semantico");
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
    "FromName": "Semantico Alerts"
  }
}
```

### Teams Notifications

Teams notifications work out-of-the-box. No additional configuration needed beyond creating recipient with webhook URL.

### Jira Notifications

Jira notifications work out-of-the-box. Configure per recipient with Jira instance details.

## Schema Configuration

### Default Schema

By default, Semantico uses `"semantico"` schema:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(builder.Configuration.GetConnectionString("SemanticoContext")!);
    // Uses "semantico" schema by default
```

### Custom Schema

Specify custom schema for multi-tenancy or environment separation:

```csharp
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        "custom_schema");
```

### Runtime Schema Selection

Load schema from configuration:

```csharp
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";

builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<YourScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        schema);
```

**In appsettings.json:**
```json
{
  "Semantico": {
    "Schema": "production_semantico"
  }
}
```

**Environment-specific schemas:**

**appsettings.Development.json:**
```json
{
  "Semantico": {
    "Schema": "dev_semantico"
  }
}
```

**appsettings.Production.json:**
```json
{
  "Semantico": {
    "Schema": "prod_semantico"
  }
}
```

## UI Path Configuration

### Custom Base Path

Change the Semantico UI path:

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/custom-path");
```

Access UI at: `http://localhost:5000/custom-path`

### Multiple Paths

Host Semantico UI at multiple paths (for different auth):

```csharp
app.UseSemanticoUI()
    .UseBasicAuthentication("admin", "admin")
    .AddBlazorUI("/semantico");

app.UseSemanticoUI()
    .UseBasicAuthentication("viewer", "readonly")
    .AddBlazorUI("/semantico-viewer");
```

## Logging Configuration

Configure logging in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Semantico": "Debug",
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

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Semantico.AI;
using Semantico.Core;
using Semantico.Core.PostgreSql;
using Semantico.UI;

var builder = WebApplication.CreateBuilder(args);

// Hangfire configuration
builder.Services.AddHangfire((provider, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("SemanticoContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10; // Adjust based on load
});

// Step 1: Add Semantico core services and configure database provider
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";
builder.Services.AddSemanticoServices(builder.Configuration, options =>
    {
        options.AddSemanticoScheduler<SemanticoScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
        options.BaseUrl = builder.Configuration["Semantico:BaseUrl"];
        options.UseAI = true; // Enable AI features (optional)
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("SemanticoContext")!,
        schema);

// Step 2: Add UI components
builder.Services.AddSemanticoUI(options =>
{
    options.AddAuthorizationProvider<CustomAuthorizationProvider>();
});

// Step 3: Add AI services (optional)
builder.Services.AddSemanticoAI(builder.Configuration);

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles(); // Required: Serves Semantico UI assets

// Semantico UI
app.UseSemanticoUI()
    .UseBasicAuthentication(
        builder.Configuration["Semantico:AdminUsername"] ?? "admin",
        builder.Configuration["Semantico:AdminPassword"] ?? "admin")
    .UseAuthorization()
    .AddBlazorUI("/semantico");

// Optional: Hangfire dashboard
app.UseHangfireDashboard("/hangfire");

app.Run();
```

**Complete appsettings.json:**
```json
{
  "ConnectionStrings": {
    "SemanticoContext": "Host=localhost;Database=semantico;Username=semantico;Password=secretpass;Pooling=true;MaxPoolSize=50"
  },
  "Semantico": {
    "Schema": "semantico",
    "AdminUsername": "admin",
    "AdminPassword": "secretpassword",
    "BaseUrl": "https://yourdomain.com/semantico"
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "app-specific-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Semantico Alerts"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Semantico": "Information",
      "Hangfire": "Warning"
    }
  }
}
```

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

Understand Semantico's extensibility points
</div>
