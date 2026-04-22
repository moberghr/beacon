<!--
Sync Impact Report:
- Version change: N/A → 1.0.0 (Initial constitution)
- Modified principles: N/A (new constitution)
- Added sections: All core principles and governance
- Removed sections: None
- Templates requiring updates:
  ✅ plan-template.md (reviewed - compatible with constitution check)
  ✅ spec-template.md (reviewed - compatible with requirements)
  ✅ tasks-template.md (reviewed - compatible with implementation approach)
- Follow-up TODOs: None
- Note: This is the initial constitution creation for Beacon project
-->

# Beacon Constitution

## Core Principles

### I. Clean Architecture
Beacon MUST follow Clean Architecture principles with clear separation of concerns:
- Core domain logic resides in `Beacon.Core` with no infrastructure dependencies
- UI and infrastructure concerns are isolated in separate projects (`Beacon.UI`, provider-specific projects)
- Domain entities implement `IChangeableEntity` for modifiable entities
- Archivable entities inherit from `BaseArchivableEntity`
- Dependencies flow inward: UI → Services → Core

**Rationale**: Clean separation ensures the domain model remains testable, portable, and free from infrastructure coupling, enabling multi-provider database support and flexible UI implementations.

### II. Schema-Agnostic Database Design
All database migrations MUST be schema-agnostic, with schema selection deferred to runtime:
- Migrations are generated using the default "beacon" schema
- Runtime schema is specified via `AddPostgreSqlBeacon(connectionString, schema)` or `AddSqlServerBeacon(connectionString, schema)`
- Each database provider (PostgreSQL, SQL Server) maintains separate migrations due to SQL dialect differences
- The `__EFMigrationsHistory` table location is set at startup per schema
- Schema is applied via `modelBuilder.HasDefaultSchema(DefaultSchema)` in `OnModelCreating`

**Rationale**: Schema-agnostic design enables multi-tenancy with schema isolation, environment-specific schemas (dev/staging/prod), and eliminates the need to regenerate migrations for different deployments.

### III. Multi-Provider Database Support
Beacon MUST maintain compatibility across multiple database providers:
- PostgreSQL (primary): Migrations in `Beacon.Core.PostgreSql`
- SQL Server: Migrations in `Beacon.Core.SqlServer`
- MySQL: Supported for query execution (consuming applications)
- Provider-specific implementations are isolated in dedicated projects
- Core entities and context remain provider-agnostic

**Rationale**: Multi-provider support maximizes adoption by allowing customers to use their existing database infrastructure without vendor lock-in.

### IV. Handler-Based Command/Query Pattern (CQRS)
All business operations MUST be implemented using MediatR handlers following CQRS principles:
- Create `internal sealed class` implementing `IRequestHandler<TRequest, TResponse>`
- Use primary constructor injection for dependencies
- Define request/response as records at file end
- One handler per use case for clear separation of concerns

**Rationale**: CQRS with MediatR provides testable, focused business logic units, clear contracts, and simplified maintenance through single-purpose handlers.

### V. Strong Typing and Explicit Contracts
All data transfer and domain models MUST use strong typing with explicit contracts:
- Prefer explicit models over primitive obsession
- Use `null!` for required string properties on entities
- Place nullable types after non-nullable properties
- Include appropriate indexes in `OnModelCreating` for frequently queried properties

**Rationale**: Strong typing catches errors at compile-time, improves IDE support, makes contracts self-documenting, and prevents runtime type errors.

### VI. Code Style Consistency
All code MUST follow consistent .NET conventions:
- **Naming**: PascalCase for classes, methods, properties; camelCase for parameters, local variables
- **Organization**: Group related files in folders based on domain/functionality
- **Error Handling**: Use exceptions with custom `BeaconException` class for domain errors
- **Imports**: System namespaces first, then third-party, then project namespaces

**Rationale**: Consistent style improves readability, reduces cognitive load, and makes the codebase accessible to all contributors.

## Database Operations Standards

### Migration Generation
All migrations MUST be generated following these procedures:
1. Ensure `Program.cs` uses default "beacon" schema temporarily for migration generation
2. Generate provider-specific migrations:
   - PostgreSQL: `dotnet ef migrations add MigrationName --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject`
   - SQL Server: `dotnet ef migrations add MigrationName --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject`
3. Verify generated migrations contain NO hardcoded schema references
4. Test migrations with multiple schema names before committing

### Database Updates
Runtime database updates are performed via:
```bash
dotnet ef database update --project Beacon.Core --startup-project Beacon.SampleProject
```
The actual schema used is determined by the runtime configuration in `Program.cs`.

## Build and Development Standards

### Build Commands
- Build solution: `dotnet build --property WarningLevel=0`
- Run application: `dotnet run --project Beacon.SampleProject`
- Watch for changes: `dotnet watch run --project Beacon.SampleProject`

### Testing Requirements
- All handler logic MUST be unit testable without infrastructure dependencies
- Test projects follow naming convention: `Beacon.Tests`
- Integration tests MUST verify provider-specific behavior across PostgreSQL and SQL Server

## Governance

### Amendment Procedure
1. Proposed changes to this constitution MUST be documented with rationale
2. Changes require validation against all dependent templates (plan, spec, tasks)
3. Version increments follow semantic versioning:
   - **MAJOR**: Backward incompatible principle removals or redefinitions
   - **MINOR**: New principles, sections, or materially expanded guidance
   - **PATCH**: Clarifications, wording refinements, typo fixes
4. All amendments require a migration plan if breaking existing practices

### Constitution Supremacy
This constitution supersedes all other development practices. When conflicts arise between this document and other guidelines:
1. Constitution principles take precedence
2. Exceptions require explicit justification in design documents
3. Complexity violations MUST be documented in the "Complexity Tracking" section of plan.md

### Compliance Verification
- All feature specifications MUST reference applicable constitutional principles
- All implementation plans MUST include a "Constitution Check" section
- Code reviews MUST verify adherence to architecture, typing, and style principles
- Any technical debt introduced MUST document which principles are deferred and why

### Runtime Guidance
For day-to-day development guidance that doesn't rise to constitutional principles, refer to `CLAUDE.md` in the repository root. This file provides practical implementation details, command references, and coding conventions that support the constitutional principles.

**Version**: 1.0.0 | **Ratified**: 2025-10-22 | **Last Amended**: 2025-10-22