# Schema-Agnostic Migrations Guide

## Overview
The Semantico architecture supports runtime schema selection. This means migrations are generated once and can be deployed to any schema name at runtime.

## How It Works

### 1. Schema is Applied at Runtime
```csharp
// In your consuming application (Program.cs)
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString, "your_custom_schema");
    // OR
    // options.UseSqlServer(connectionString, "your_custom_schema");

    options.AddSemanticoScheduler<YourScheduler>();
});
```

### 2. Schema is Set via HasDefaultSchema
The schema is applied in the context's `OnModelCreating` method:
```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.HasDefaultSchema(DefaultSchema); // Applied dynamically
    base.OnModelCreating(modelBuilder);
}
```

### 3. Migrations History Table Location
The `__EFMigrationsHistory` table location is set at startup:
```csharp
options.UseNpgsql(connectionString,
    builder => builder.MigrationsHistoryTable("__EFMigrationsHistory", schema))
```

## Generating Schema-Agnostic Migrations

### Important: Migrations are Provider-Specific
- **PostgreSQL migrations** go in: `Semantico.Core.PostgreSql`
- **SQL Server migrations** go in: `Semantico.Core.SqlServer`

Each provider has different SQL syntax, so they need separate migrations.

### Steps to Generate Migrations

1. **Temporarily set the default schema in Program.cs** (for migration generation):
   ```csharp
   // Use default schema for generating migrations
   builder.Services.AddSemantico(builder.Configuration, options =>
   {
       options.UsePostgreSql(connectionString); // Uses "semantico" by default
       options.AddSemanticoScheduler<YourScheduler>();
   });
   ```

2. **Generate the migration**:
   ```bash
   # For PostgreSQL
   dotnet ef migrations add YourMigrationName \
     --project Semantico.Core.PostgreSql \
     --startup-project Semantico.SampleProject

   # For SQL Server
   dotnet ef migrations add YourMigrationName \
     --project Semantico.Core.SqlServer \
     --startup-project Semantico.SampleProject
   ```

3. **Restore your custom schema** (if you were testing with one):
   ```csharp
   builder.Services.AddSemantico(builder.Configuration, options =>
   {
       options.UsePostgreSql(connectionString, "test");
       options.AddSemanticoScheduler<YourScheduler>();
   });
   ```

## How Consuming Projects Use Custom Schemas

### Example: Multiple Tenants with Different Schemas
```csharp
public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    var tenantSchema = GetTenantSchema(); // e.g., "tenant1", "tenant2", etc.

    services.AddSemantico(configuration, options =>
    {
        options.UsePostgreSql(configuration.GetConnectionString("Database")!, tenantSchema);
        options.AddSemanticoScheduler<YourScheduler>();
    });
}
```

### Example: Development vs Production Schemas
```csharp
// appsettings.Development.json
{
  "Semantico": {
    "Schema": "dev_semantico"
  }
}

// appsettings.Production.json
{
  "Semantico": {
    "Schema": "prod_semantico"
  }
}

// Program.cs
var schema = builder.Configuration["Semantico:Schema"] ?? "semantico";
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString, schema);
    options.AddSemanticoScheduler<YourScheduler>();
});
```

## Automatic Schema Creation
The `UseSemantico()` method automatically creates the schema if it doesn't exist:
```csharp
// In ServiceConfiguration.cs
public static void UseSemantico(IServiceProvider serviceProvider)
{
    // ...
    if (!string.IsNullOrEmpty(schema) && schema != "public")
    {
        context.Database.ExecuteSqlRaw($"CREATE SCHEMA IF NOT EXISTS \"{schema}\"");
    }
    context.Database.Migrate();
}
```

## Verification

### Check that migrations don't contain hardcoded schemas:
```bash
# The migration files should NOT contain schema references like:
# schema: "my_hardcoded_schema"

# They SHOULD only use modelBuilder.HasDefaultSchema() which is set at runtime
```

### Test with different schemas:
```csharp
// Test 1: Default schema
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString); // Uses "semantico" by default
    options.AddSemanticoScheduler<YourScheduler>();
});

// Test 2: Custom schema
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString, "custom");
    options.AddSemanticoScheduler<YourScheduler>();
});

// Test 3: Per-tenant schema
builder.Services.AddSemantico(builder.Configuration, options =>
{
    options.UsePostgreSql(connectionString, $"tenant_{tenantId}");
    options.AddSemanticoScheduler<YourScheduler>();
});
```

## Benefits
1. ✅ Single set of migrations for all deployments
2. ✅ Runtime schema configuration
3. ✅ Multi-tenant support with schema isolation
4. ✅ Environment-specific schemas (dev/staging/prod)
5. ✅ No need to regenerate migrations for different schemas