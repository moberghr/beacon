# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands
- Build solution: `dotnet build --property WarningLevel=0`
- Run application: `dotnet run --project Semantico.SampleProject`
- Watch for changes: `dotnet watch run --project Semantico.SampleProject`

## Code Style Guidelines
- **Naming**: PascalCase for classes, methods, properties; camelCase for parameters, local variables
- **Organization**: Group related files in folders based on domain/functionality
- **Architecture**: Follow Clean Architecture principles with Core containing domain logic
- **Error Handling**: Use exceptions with custom SemanticoException class for domain errors
- **Imports**: Organize by System namespaces first, then third-party, then project namespaces
- **Types**: Prefer strong typing with explicit models for data transfer

### Entity Design
- Implement `IChangeableEntity` for modifiable entities
- Inherit from `BaseArchivableEntity` for archivable entities
- Use `null!` for required string properties
- Place nullable types after non-nullable properties
- Add appropriate indexes in context's OnModelCreating method for properties that will be queried frequently

### Database Operations
```bash
# Generate schema-agnostic migration (ensure Program.cs uses default "semantico" schema)
dotnet ef migrations add MigrationName --project Semantico.Core --startup-project Semantico.SampleProject

# For PostgreSQL provider specifically
dotnet ef migrations add MigrationName --project Semantico.Core.PostgreSql --startup-project Semantico.SampleProject

# For SQL Server provider specifically
dotnet ef migrations add MigrationName --project Semantico.Core.SqlServer --startup-project Semantico.SampleProject

# Update database (uses schema specified in Program.cs)
dotnet ef database update --project Semantico.Core --startup-project Semantico.SampleProject

# IMPORTANT: Migrations are schema-agnostic. The schema is specified at runtime via:
# services.AddPostgreSqlSemantico(connectionString, "your_schema_name")
```

### Handler Structure
- Create `internal sealed class` implementing `IRequestHandler<TRequest, TResponse>`
- Define request/response as records at file end (not with "// Request/Response at end of file" comment)
- Use primary constructor injection for dependencies

## Project Structure
- Semantico.Core: Core domain model, services, data access
- Semantico.UI: Blazor UI components
- Semantico.SampleProject: Sample implementation/application
