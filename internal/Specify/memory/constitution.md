# Specify Constitution

## Core Principles

### I. Specification-Driven Development
All features begin with a written specification before implementation. Specifications must be concrete, testable, and include clear success criteria. No code is written until the specification is approved and complete.

### II. Clean Architecture Principles
Follow Clean Architecture with Core containing domain logic. Entity design must implement `IChangeableEntity` for modifiable entities and inherit from `BaseArchivableEntity` for archivable entities. Database operations require proper migrations and indexing for frequently queried properties.

### III. Test-First Development (NON-NEGOTIABLE)
TDD mandatory: Tests written → User approved → Tests fail → Then implement. Red-Green-Refactor cycle strictly enforced. All handlers must have corresponding tests before implementation.

### IV. Strong Typing and Code Quality
Prefer strong typing with explicit models for data transfer. Use PascalCase for classes, methods, properties; camelCase for parameters, local variables. Organize imports by System namespaces first, then third-party, then project namespaces.

### V. Error Handling and Observability
Use exceptions with custom BeaconException class for domain errors. Structured logging required for debugging and monitoring. All operations must provide clear error messages and proper exception handling.

## Development Standards

### Entity Design Requirements
- Use `null!` for required string properties
- Place nullable types after non-nullable properties
- Add appropriate indexes in context's OnModelCreating method for properties that will be queried frequently
- Group related files in folders based on domain/functionality

### Handler Structure Standards
- Create `internal sealed class` implementing `IRequestHandler<TRequest, TResponse>`
- Define request/response as records at file end
- Use primary constructor injection for dependencies
- Follow established patterns in existing handlers

### Database Operations
Database changes must follow proper migration procedures:
- Generate migration: `dotnet ef migrations add MigrationName --project Beacon.Core --startup-project Beacon.SampleProject`
- Update database: `dotnet ef database update --project Beacon.Core --startup-project Beacon.SampleProject`

## Build and Development Workflow

### Build Commands
- Build solution: `dotnet build --property WarningLevel=0`
- Run application: `dotnet run --project Beacon.SampleProject`
- Watch for changes: `dotnet watch run --project Beacon.SampleProject`

### Feature Development Process
1. Create specification using Spec-Driven Development lifecycle
2. Get specification approval before implementation
3. Write tests first (TDD)
4. Implement to make tests pass
5. Run quality checks and build verification

## Project Structure
- **Beacon.Core**: Core domain model, services, data access
- **Beacon.UI**: Blazor UI components  
- **Beacon.SampleProject**: Sample implementation/application

## Governance

Constitution supersedes all other practices. All code reviews must verify compliance with these principles. Amendments require documentation, approval, and migration plan.

Complexity must be justified against business value. Use CLAUDE.md for runtime development guidance and specific technical implementation details.

**Version**: 1.0.0 | **Ratified**: 2025-09-04 | **Last Amended**: 2025-09-04