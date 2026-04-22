# Research: Comprehensive Documentation System

**Date**: 2025-10-22
**Feature**: Comprehensive Documentation System
**Branch**: `003-comprehensive-documentation`

## Purpose

This research document identifies existing documentation patterns, project structure, and technical context to inform the comprehensive documentation system design.

## Current Documentation State

### Existing Documentation Files

| File | Location | Purpose | Status |
|------|----------|---------|--------|
| README.md | `/Users/mirkobudimir/Dev/beacon/README.md` | High-level overview, Getting Started | ✅ Exists |
| CLAUDE.md | `/Users/mirkobudimir/Dev/beacon/CLAUDE.md` | Development guidelines for Claude Code | ✅ Exists |
| SCHEMA_AGNOSTIC_MIGRATIONS.md | Root | Advanced schema migration guide | ✅ Exists |

### Documentation Gaps (High Priority)

- ❌ No `/docs` folder structure
- ❌ No architecture/design documentation
- ❌ No feature-specific guides (query chaining, multi-step queries, cross-database)
- ❌ No troubleshooting guide
- ❌ No deployment guide
- ❌ No GitHub Pages marketing site

## Project Structure Analysis

### Solution Architecture

```
Beacon/
├── Beacon.Core/                    # Core domain logic
│   ├── Data/
│   │   ├── Entities/                  # Domain models (Project, Query, Subscription, etc.)
│   │   ├── Enums/
│   │   └── BeaconContext.cs       # EF Core DbContext
│   ├── Services/                      # Domain services (IProjectService, IQueryService, etc.)
│   ├── Models/                        # DTOs (ProjectData, QueryData, etc.)
│   ├── Features/
│   │   └── DataMigration/            # Data migration feature
│   ├── Adapters/                      # Teams, Jira, Email integration
│   └── Worker/                        # Hangfire job scheduling
│
├── Beacon.UI/                      # Blazor UI components
│   ├── Components/
│   │   ├── Pages/                     # Main pages (Projects, Queries, Subscriptions, etc.)
│   │   └── Shared/                    # Reusable components
│   └── wwwroot/                       # Static assets
│
├── Beacon.UI.AspNet/               # ASP.NET integration
├── Beacon.SampleProject/           # Sample application
├── Beacon.Core.PostgreSql/         # PostgreSQL provider
├── Beacon.Core.SqlServer/          # SQL Server provider
└── Beacon.Tests/                   # Unit tests
```

### Key Features to Document

Based on entity and service analysis:

1. **Projects** - Database connections (PostgreSQL, SQL Server, MySQL)
2. **Queries** - Multi-step query chains with cross-project support
3. **Query Steps** - Individual SQL steps with parameters
4. **Subscriptions** - Scheduled query execution (cron expressions)
5. **Recipients** - Email, Teams, Jira notification targets
6. **Notifications** - Result delivery system
7. **Data Migrations** - Schema-agnostic data migration
8. **Query Parameters** - Dynamic placeholder replacement
9. **Query Execution History** - Audit trail

### Advanced Capabilities Requiring Documentation

- **Cross-Project Query Chaining**: Queries can reference other projects' databases
- **Multi-Step Queries**: Sequential execution with result aggregation
- **Cross-Database Queries**: Mix PostgreSQL, SQL Server, MySQL in single workflow
- **Final Query Aggregation**: Combine results from multiple steps using `@result1`, `@result2`
- **Schema-Agnostic Architecture**: Runtime schema configuration for multi-tenancy
- **Query Execution Preview**: Validate queries before scheduling

## Technology Stack

| Layer | Technology | Version | Notes |
|-------|-----------|---------|-------|
| **Runtime** | .NET | 9.0 | Latest LTS |
| **UI Framework** | Blazor Server | 9.0 | Interactive server mode |
| **UI Components** | MudBlazor | 8.11 | Material Design components |
| **Database** | EF Core | 9.0 | Multi-provider support |
| **Job Scheduling** | Hangfire | 1.8 | PostgreSQL storage backend |
| **Notifications** | Teams, Jira, Email | - | Adapter pattern |
| **Data Export** | CsvHelper, ClosedXML | - | CSV and Excel export |
| **Expression Parsing** | Cronos, CronExpressionDescriptor | - | Cron schedule support |

## Configuration & Deployment

### Build Commands (from CLAUDE.md)

```bash
# Build solution
dotnet build --property WarningLevel=0

# Run application
dotnet run --project Beacon.SampleProject

# Watch for changes
dotnet watch run --project Beacon.SampleProject
```

### Database Setup

```bash
# Generate provider-specific migrations
dotnet ef migrations add MigrationName --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject
dotnet ef migrations add MigrationName --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject

# Update database
dotnet ef database update --project Beacon.Core --startup-project Beacon.SampleProject
```

### Environment Configuration

**Required:**
- `ConnectionStrings__BeaconContext` - PostgreSQL connection string for Beacon base database

**Optional:**
- `SendGridSettings__ApiKey` - SendGrid API key for email notifications
- `SendGridSettings__SenderEmail` - Sender email address
- `SendGridSettings__SenderName` - Sender display name
- `Beacon:Schema` - Custom schema name (default: "beacon")

### Docker Deployment

Current deployment method uses Docker Compose (example in README.md):

```yaml
services:
  beacon:
    image: 'ghcr.io/moberghr/beacon:latest'
    ports:
      - 8080:80
    environment:
      - ConnectionStrings__BeaconContext=...
      - SendGridSettings__ApiKey=...
```

## Documentation Technology Recommendations

### Static Site Generator: Jekyll

**Decision Rationale** (from clarifications):
- GitHub's native generator with zero-config deployment
- Automatic builds on push to main branch
- No external build pipeline required
- Standard for GitHub Pages documentation sites
- Ruby-based with extensive theme ecosystem

**Recommended Theme**:
- **Just the Docs** - Clean, documentation-focused Jekyll theme
- Features: Built-in navigation, search (optional via Lunr.js), mobile-responsive, WCAG 2.1 compatible

### Documentation Structure

Based on constitutional principles and feature analysis:

```
docs/
├── index.md                           # Homepage (marketing content)
├── _config.yml                        # Jekyll configuration
├── getting-started/
│   ├── installation.md               # Docker Compose setup
│   ├── quick-start.md                # First query in 30 minutes
│   └── configuration.md              # Environment variables, connection strings
├── features/
│   ├── projects.md                   # Project management
│   ├── queries.md                    # Query creation basics
│   ├── multi-step-queries.md        # Advanced: multi-step with aggregation
│   ├── subscriptions.md              # Scheduling with cron
│   ├── notifications.md              # Email, Teams, Jira setup
│   ├── recipients.md                 # Recipient management
│   ├── parameters.md                 # Dynamic query parameters
│   └── data-migration.md             # Data migration feature
├── advanced/
│   ├── query-chaining.md             # Cross-project query chains
│   ├── cross-database.md             # Multi-database queries
│   ├── multi-tenant.md               # Schema-agnostic deployments
│   ├── custom-providers.md           # Extending database providers
│   └── architecture.md               # Clean Architecture overview
├── api/
│   └── services.md                   # Service interfaces reference
├── troubleshooting/
│   ├── common-issues.md              # FAQ and solutions
│   ├── debugging.md                  # Logging and diagnostics
│   └── performance.md                # Performance tuning
└── contributing/
    └── guidelines.md                 # Contribution guide
```

### Accessibility: WCAG 2.1 Level A

**Decision Rationale** (from clarifications):
- Minimum legal compliance standard
- Achievable with semantic HTML and basic best practices
- Automated testing via pa11y or axe-core

**Key Requirements:**
- Semantic HTML5 structure
- Keyboard navigation support
- Alt text for all images
- Sufficient color contrast (4.5:1 for normal text)
- Skip navigation links

### Search Strategy: No Built-In Search

**Decision Rationale** (from clarifications):
- Rely on browser Ctrl+F and clear navigation
- Reduces complexity (no JavaScript library dependencies)
- GitHub Pages native search not required for documentation scope

**Mitigation**:
- Comprehensive table of contents
- Clear navigation hierarchy
- Well-structured headings for browser find

## Constitutional Compliance

### Relevant Principles

1. **Code Style Consistency** (VI):
   - Documentation examples follow PascalCase/camelCase conventions
   - Configuration examples include explanatory comments
   - Imports organized: System → third-party → project

2. **Strong Typing and Explicit Contracts** (V):
   - Service interface documentation shows explicit types
   - Example code uses strongly-typed models
   - No `dynamic` or `var` without clear intent

3. **Clean Architecture** (I):
   - Documentation structure mirrors: Core → Services → UI
   - Examples show proper dependency flow
   - Entity design patterns documented

4. **Schema-Agnostic Database Design** (II):
   - Migration guides emphasize runtime schema configuration
   - Multi-tenancy deployment scenarios documented
   - Provider-specific migration instructions clear

### No Constitutional Violations

This documentation feature does not introduce code changes, only content. No architectural principles are violated.

## Reusable Components

### Existing Patterns to Reference

1. **Docker Compose Examples** - README.md has working example
2. **Migration Commands** - SCHEMA_AGNOSTIC_MIGRATIONS.md provides comprehensive guide
3. **Build Commands** - CLAUDE.md documents standard workflows
4. **Code Style** - CLAUDE.md provides detailed style guide

### Code Examples to Include

**High Priority:**
- Complete Docker Compose configuration
- Creating first project and query
- Setting up email/Teams/Jira notifications
- Defining multi-step query with aggregation
- Configuring cron subscription

**Medium Priority:**
- Query parameter usage
- Cross-project query chaining
- Multi-tenant schema configuration
- Custom notification adapter

## Visual Assets Required

### Screenshots

1. Main dashboard (Home.razor)
2. Project creation dialog
3. Query builder interface
4. Subscription configuration (cron expression)
5. Notification history view
6. Data migration job creation

### Diagrams

1. Architecture overview (Clean Architecture layers)
2. Multi-step query execution flow
3. Cross-database query orchestration
4. Schema-agnostic deployment topology
5. Notification delivery workflow

## Recommended Documentation Order

### Phase 1: Essential Documentation (P1 User Story)

1. Enhanced README.md with clear overview and quick start
2. `docs/getting-started/installation.md`
3. `docs/getting-started/quick-start.md`
4. `docs/features/projects.md`
5. `docs/features/queries.md`
6. `docs/features/subscriptions.md`
7. `docs/features/notifications.md`

### Phase 2: Feature Discovery (P2 User Story)

1. All remaining feature guides
2. Advanced configuration guides
3. Troubleshooting guide
4. Service API reference

### Phase 3: Marketing Site (P3 User Story)

1. Jekyll configuration and homepage
2. Use case showcase (3 examples)
3. Navigation and site structure
4. Mobile-responsive styling

### Phase 4: Advanced Topics (P4 User Story)

1. Architecture deep-dive
2. Multi-tenant deployment guide
3. Extensibility guides
4. Performance tuning

## Success Metrics

From spec SC-001 to SC-010, key measurable outcomes:

- **30-minute onboarding**: Quick start guide must be completable in under 30 minutes
- **2-minute findability**: 90% of common questions answerable within 2 minutes using navigation
- **60% support reduction**: Documentation should reduce "how do I..." GitHub issues by 60%
- **70% conversion**: Marketing site should convert 70% of visitors to click installation guide
- **100% feature coverage**: Every feature in constitution and codebase documented
- **2-second load time**: GitHub Pages site loads in under 2 seconds

## Next Steps

1. Create data model for documentation structure
2. Define quickstart guide content
3. Generate implementation plan with file-by-file breakdown
4. Create tasks.md for phased implementation
