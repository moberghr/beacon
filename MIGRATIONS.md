# Database Migrations in Beacon

This document provides instructions on how to create and apply database migrations in the Beacon project.

Beacon ships **dual-provider** migrations: every schema change requires a NEW migration in **both**
`Beacon.Core.PostgreSql` and `Beacon.Core.SqlServer`. The two providers have different SQL syntax, so their
migrations live in separate projects and must be kept in lockstep. **Never edit a migration that has already
been committed — always add a new one.**

## Creating Migrations

When you need to make changes to the database schema, follow these steps:

1. Make the necessary changes to the entity models in `Beacon.Core` (entities + fluent configuration in
   `BeaconContext.OnModelCreating`).
2. Create a migration for **each** provider:

```bash
# PostgreSQL
dotnet ef migrations add MigrationName --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject

# SQL Server
dotnet ef migrations add MigrationName --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject
```

Replace `MigrationName` with a descriptive name for your migration (e.g., `AddUserTable`, `UpdateProductSchema`).
Use the same name for both providers so the pair is easy to correlate.

## Common Migration Scenarios

### Adding a New Property to an Entity

1. Add the property to the entity class
2. Create a migration (both providers) as described above
3. The migration will include an `AddColumn` operation

### Renaming a Property

1. Use the `RenameColumn` method in the migration's `Up` method
2. Add the reverse operation in the `Down` method

### Custom Data Transformations

For complex data transformations, you can add raw SQL in the migration:

```csharp
migrationBuilder.Sql(@"
    UPDATE tablename
    SET column = value
    WHERE condition
");
```

## Applying Migrations

To apply pending migrations to the database (run against the provider your deployment uses):

```bash
# PostgreSQL
dotnet ef database update --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject

# SQL Server
dotnet ef database update --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject
```

> Note: in normal operation `UseBeacon()` calls `context.Database.Migrate()` at startup, so the host applies
> pending migrations automatically. The explicit `database update` commands are mainly for tooling and CI.

## Reverting Migrations

To revert to a specific migration:

```bash
dotnet ef database update MigrationName --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject
```

To revert the most recent migration, update to the previous one:

```bash
dotnet ef database update PreviousMigrationName --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject
```

(Use the matching `--project Beacon.Core.SqlServer` invocation for SQL Server.)

## Migration History

To list all migrations and their status:

```bash
dotnet ef migrations list --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject
dotnet ef migrations list --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject
```
