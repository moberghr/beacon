# Implementation Plan: Data Migration Tool

**Branch**: `001-data-migrator` | **Date**: 2025-09-04 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-data-migrator/spec.md`

## Execution Flow (/plan command scope)
```
1. Load feature spec from Input path
   → If not found: ERROR "No feature spec at {path}"
2. Fill Technical Context (scan for NEEDS CLARIFICATION)
   → Detect Project Type from context (web=frontend+backend, mobile=app+api)
   → Set Structure Decision based on project type
3. Evaluate Constitution Check section below
   → If violations exist: Document in Complexity Tracking
   → If no justification possible: ERROR "Simplify approach first"
   → Update Progress Tracking: Initial Constitution Check
4. Execute Phase 0 → research.md
   → If NEEDS CLARIFICATION remain: ERROR "Resolve unknowns"
5. Execute Phase 1 → contracts, data-model.md, quickstart.md, agent-specific template file (e.g., `CLAUDE.md` for Claude Code, `.github/copilot-instructions.md` for GitHub Copilot, or `GEMINI.md` for Gemini CLI).
6. Re-evaluate Constitution Check section
   → If new violations: Refactor design, return to Phase 1
   → Update Progress Tracking: Post-Design Constitution Check
7. Plan Phase 2 → Describe task generation approach (DO NOT create tasks.md)
8. STOP - Ready for /tasks command
```

**IMPORTANT**: The /plan command STOPS at step 7. Phases 2-4 are executed by other commands:
- Phase 2: /tasks command creates tasks.md
- Phase 3-4: Implementation execution (manual or via tools)

## Summary
Data Migration Tool that extends existing query execution layer to support data migration destinations alongside notifications. Reuses QueryExecutionHistory table and @result syntax from notifications system. Provides UI for configuring migration jobs with scheduling and destination database selection.

## Technical Context
**Language/Version**: C# .NET (existing project stack)  
**Primary Dependencies**: Entity Framework, MediatR, Blazor UI (existing dependencies)  
**Storage**: Existing database with QueryExecutionHistory table extension  
**Testing**: NUnit/xUnit (following existing test patterns)  
**Target Platform**: Web application (Blazor Server/WASM)
**Project Type**: web (extends existing Semantico.UI and Semantico.Core)  
**Performance Goals**: Handle migration jobs similar to notification processing performance  
**Constraints**: Reuse existing query execution infrastructure, maintain UI consistency  
**Scale/Scope**: Extend existing application with migration capabilities

## Constitution Check
*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Simplicity**:
- Projects: 2 (Semantico.Core for logic, Semantico.UI for interface - reusing existing)
- Using framework directly? (yes - direct EF, MediatR, Blazor usage)
- Single data model? (yes - extending existing entities)
- Avoiding patterns? (yes - no new repository patterns, reuse existing handlers)

**Architecture**:
- EVERY feature as library? (extends existing library structure in Semantico.Core)
- Libraries listed: Migration handlers in Semantico.Core, UI components in Semantico.UI
- CLI per library: N/A (web application, not CLI-focused)
- Library docs: Will update existing documentation patterns

**Testing (NON-NEGOTIABLE)**:
- RED-GREEN-Refactor cycle enforced? (yes - TDD mandatory per constitution)
- Git commits show tests before implementation? (yes - constitutional requirement)
- Order: Contract→Integration→E2E→Unit strictly followed? (yes)
- Real dependencies used? (yes - actual database, existing QueryExecutionHistory)
- Integration tests for: extending query execution, new migration contracts, UI integration
- FORBIDDEN: Implementation before test, skipping RED phase

**Observability**:
- Structured logging included? (yes - reuse existing logging infrastructure)
- Frontend logs → backend? (yes - existing Blazor logging patterns)
- Error context sufficient? (yes - SemanticoException patterns)

**Versioning**:
- Version number assigned? (extends existing application version)
- BUILD increments on every change? (follows existing versioning)
- Breaking changes handled? (database migrations, backwards compatibility)

## Project Structure

### Documentation (this feature)
```
specs/001-data-migrator/
├── plan.md              # This file (/plan command output)
├── research.md          # Phase 0 output (/plan command)
├── data-model.md        # Phase 1 output (/plan command)
├── quickstart.md        # Phase 1 output (/plan command)
├── contracts/           # Phase 1 output (/plan command)
└── tasks.md             # Phase 2 output (/tasks command - NOT created by /plan)
```

### Source Code (repository root)
```
# Extends existing Semantico project structure
Semantico.Core/
├── Features/
│   └── DataMigration/           # New feature area
│       ├── Entities/           # Migration job entities
│       ├── Handlers/           # MediatR handlers
│       └── Services/           # Migration execution services
└── Extensions/                  # Extend QueryExecutionHistory

Semantico.UI/
└── Components/
    └── Pages/
        └── DataMigration/       # New UI pages
            ├── MigrationList.razor
            ├── CreateMigration.razor
            └── MigrationHistory.razor
```

**Structure Decision**: Extends existing web application structure (Semantico.Core + Semantico.UI)

## Phase 0: Outline & Research

Based on Technical Context, no major unknowns requiring research. Key areas to validate:

1. **Query Execution Layer Integration**:
   - Research current query execution patterns
   - Identify extension points for migration destinations
   - Validate @result syntax compatibility

2. **Entity Extension Strategy**:
   - Research QueryExecutionHistory table structure
   - Plan database migration for new migration-specific fields
   - Validate relationship with existing Project table

3. **UI Component Patterns**:
   - Research existing Blazor component patterns
   - Identify reusable components for migration configuration
   - Plan navigation integration

**Output**: research.md with current architecture analysis and extension approach

## Phase 1: Design & Contracts
*Prerequisites: research.md complete*

1. **Extract entities from feature spec** → `data-model.md`:
   - MigrationJob entity (extends/relates to existing entities)
   - Extended QueryExecutionHistory for migration tracking
   - Relationship to Project entity for destinations

2. **Generate API contracts** from functional requirements:
   - MediatR request/response patterns (not REST - internal handlers)
   - CreateMigrationJobHandler contracts
   - ExecuteMigrationHandler contracts
   - Migration history query contracts

3. **Generate contract tests** from contracts:
   - Handler contract tests (must fail initially)
   - Integration tests for query execution extension
   - UI component integration tests

4. **Extract test scenarios** from user stories:
   - Create migration job flow
   - Execute migration with history tracking
   - UI navigation and form validation

5. **Update CLAUDE.md incrementally**:
   - Add migration-specific development guidance
   - Document entity relationship patterns
   - Update handler structure examples

**Output**: data-model.md, /contracts/*, failing tests, quickstart.md, updated CLAUDE.md

## Phase 2: Task Planning Approach
*This section describes what the /tasks command will do - DO NOT execute during /plan*

**Task Generation Strategy**:
- Generate database migration tasks for new entities
- Create MediatR handler tasks (CreateMigrationJob, ExecuteMigration, etc.)
- Create entity model tasks
- Generate UI component tasks (list, create, history pages)
- Integration tasks for query execution extension

**Ordering Strategy**:
- Database migration first
- Entity models before handlers
- Handler contracts before implementation
- UI components after backend completion
- Integration tests throughout

**Estimated Output**: 20-25 numbered, ordered tasks in tasks.md

**IMPORTANT**: This phase is executed by the /tasks command, NOT by /plan

## Phase 3+: Future Implementation
*These phases are beyond the scope of the /plan command*

**Phase 3**: Task execution (/tasks command creates tasks.md)  
**Phase 4**: Implementation (execute tasks.md following constitutional principles)  
**Phase 5**: Validation (run tests, execute quickstart.md, performance validation)

## Complexity Tracking
*No constitutional violations identified - feature extends existing architecture cleanly*

## Progress Tracking
*This checklist is updated during execution flow*

**Phase Status**:
- [x] Phase 0: Research complete (/plan command)
- [x] Phase 1: Design complete (/plan command)
- [x] Phase 2: Task planning complete (/plan command - describe approach only)
- [ ] Phase 3: Tasks generated (/tasks command)
- [ ] Phase 4: Implementation complete
- [ ] Phase 5: Validation passed

**Gate Status**:
- [x] Initial Constitution Check: PASS
- [x] Post-Design Constitution Check: PASS
- [x] All NEEDS CLARIFICATION resolved (from user inputs)
- [x] Complexity deviations documented (none required)

---
*Based on Specify Constitution v1.0.0 - See `/memory/constitution.md`*