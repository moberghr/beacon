# Database Migrations in Beacon

This document provides instructions on how to create and apply database migrations in the Beacon project.

## Creating Migrations

When you need to make changes to the database schema, follow these steps to create a migration:

1. Make the necessary changes to the entity models in the `Beacon.Core/Data/Entities` directory
2. Run the following command to create a migration:

```bash
dotnet ef migrations add MigrationName --project Beacon.Core --startup-project Beacon.SampleProject
```

Replace `MigrationName` with a descriptive name for your migration (e.g., `AddUserTable`, `UpdateProductSchema`).

## Common Migration Scenarios

### Adding a New Property to an Entity

1. Add the property to the entity class
2. Create a migration as described above
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

To apply pending migrations to the database:

```bash
dotnet ef database update --project Beacon.Core --startup-project Beacon.SampleProject
```

## Reverting Migrations

To revert to a specific migration:

```bash
dotnet ef database update MigrationName --project Beacon.Core --startup-project Beacon.SampleProject
```

To revert the most recent migration:

```bash
dotnet ef database update PreviousMigrationName --project Beacon.Core --startup-project Beacon.SampleProject
```

## Migration History

To list all migrations and their status:

```bash
dotnet ef migrations list --project Beacon.Core --startup-project Beacon.SampleProject
```