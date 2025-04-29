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

## Project Structure
- Semantico.Core: Core domain model, services, data access
- Semantico.UI: Blazor UI components
- Semantico.SampleProject: Sample implementation/application
