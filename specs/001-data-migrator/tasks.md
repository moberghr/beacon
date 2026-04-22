# Tasks: Data Migration Tool

**Input**: Design documents from `/specs/001-data-migrator/`
**Prerequisites**: plan.md (required), research.md, data-model.md, contracts/

## Execution Flow (main)
```
1. Load plan.md from feature directory
   → If not found: ERROR "No implementation plan found"
   → Extract: tech stack, libraries, structure
2. Load optional design documents:
   → data-model.md: Extract entities → model tasks
   → contracts/: Each file → contract test task
   → research.md: Extract decisions → setup tasks
3. Generate tasks by category:
   → Setup: project init, dependencies, linting
   → Tests: contract tests, integration tests
   → Core: models, services, CLI commands
   → Integration: DB, middleware, logging
   → Polish: unit tests, performance, docs
4. Apply task rules:
   → Different files = mark [P] for parallel
   → Same file = sequential (no [P])
   → Tests before implementation (TDD)
5. Number tasks sequentially (T001, T002...)
6. Generate dependency graph
7. Create parallel execution examples
8. Validate task completeness:
   → All contracts have tests?
   → All entities have models?
   → All endpoints implemented?
9. Return: SUCCESS (tasks ready for execution)
```

## Format: `[ID] [P?] Description`
- **[P]**: Can run in parallel (different files, no dependencies)
- Include exact file paths in descriptions

## Path Conventions
- **Web app**: Extends existing `Beacon.Core/` and `Beacon.UI/` structure
- Paths based on existing project structure from plan.md

## Phase 3.1: Setup & Database
- [ ] T001 Create database migration for MigrationJob and MigrationExecution entities
- [ ] T002 [P] Add MigrationMode and MigrationStatus enums to Beacon.Core/Enums/
- [ ] T003 Configure entity relationships in BeaconContext.OnModelCreating

## Phase 3.2: Tests First (TDD) ⚠️ MUST COMPLETE BEFORE 3.3
**CRITICAL: These tests MUST be written and MUST FAIL before ANY implementation**

### Handler Contract Tests (Parallel - Different Test Files)
- [ ] T004 [P] Contract test CreateMigrationJobHandler in tests/Features/DataMigration/CreateMigrationJobHandlerTests.cs
- [ ] T005 [P] Contract test ExecuteMigrationJobHandler in tests/Features/DataMigration/ExecuteMigrationJobHandlerTests.cs
- [ ] T006 [P] Contract test GetMigrationJobsHandler in tests/Features/DataMigration/GetMigrationJobsHandlerTests.cs
- [ ] T007 [P] Contract test GetMigrationExecutionsHandler in tests/Features/DataMigration/GetMigrationExecutionsHandlerTests.cs
- [ ] T008 [P] Contract test UpdateMigrationJobHandler in tests/Features/DataMigration/UpdateMigrationJobHandlerTests.cs
- [ ] T009 [P] Contract test DeleteMigrationJobHandler in tests/Features/DataMigration/DeleteMigrationJobHandlerTests.cs

### UI Component Contract Tests (Parallel - Different Test Files)
- [ ] T010 [P] Contract test MigrationJobList component in tests/UI/Components/DataMigration/MigrationJobListTests.cs
- [ ] T011 [P] Contract test MigrationJobForm component in tests/UI/Components/DataMigration/MigrationJobFormTests.cs
- [ ] T012 [P] Contract test MigrationExecutionHistory component in tests/UI/Components/DataMigration/MigrationExecutionHistoryTests.cs
- [ ] T013 [P] Contract test MigrationListPage in tests/UI/Pages/DataMigration/MigrationListPageTests.cs

### Integration Tests from User Stories (Parallel - Different Test Files)
- [ ] T014 [P] Integration test: Create migration job with validation in tests/Integration/DataMigration/CreateMigrationJobIntegrationTests.cs
- [ ] T015 [P] Integration test: Execute migration job with real data in tests/Integration/DataMigration/ExecuteMigrationJobIntegrationTests.cs
- [ ] T016 [P] Integration test: Migration history tracking in tests/Integration/DataMigration/MigrationHistoryIntegrationTests.cs
- [ ] T017 [P] Integration test: UI navigation and form submission in tests/Integration/DataMigration/MigrationUIIntegrationTests.cs

## Phase 3.3: Core Implementation (ONLY after tests are failing)

### Entity Models (Parallel - Different Files)
- [ ] T018 [P] MigrationJob entity in Beacon.Core/Features/DataMigration/Entities/MigrationJob.cs
- [ ] T019 [P] MigrationExecution entity in Beacon.Core/Features/DataMigration/Entities/MigrationExecution.cs
- [ ] T020 [P] MigrationJobDto record in Beacon.Core/Features/DataMigration/DTOs/MigrationJobDto.cs
- [ ] T021 [P] MigrationExecutionDto record in Beacon.Core/Features/DataMigration/DTOs/MigrationExecutionDto.cs

### Handler Request/Response Records (Parallel - Different Files)
- [ ] T022 [P] CreateMigrationJobRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/CreateMigrationJob.cs
- [ ] T023 [P] ExecuteMigrationJobRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/ExecuteMigrationJob.cs
- [ ] T024 [P] GetMigrationJobsRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/GetMigrationJobs.cs
- [ ] T025 [P] GetMigrationExecutionsRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/GetMigrationExecutions.cs
- [ ] T026 [P] UpdateMigrationJobRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/UpdateMigrationJob.cs
- [ ] T027 [P] DeleteMigrationJobRequest/Response records in Beacon.Core/Features/DataMigration/Handlers/DeleteMigrationJob.cs

### Handler Implementations (Sequential - May share dependencies)
- [ ] T028 CreateMigrationJobHandler implementation in Beacon.Core/Features/DataMigration/Handlers/CreateMigrationJob.cs
- [ ] T029 ExecuteMigrationJobHandler implementation in Beacon.Core/Features/DataMigration/Handlers/ExecuteMigrationJob.cs
- [ ] T030 GetMigrationJobsHandler implementation in Beacon.Core/Features/DataMigration/Handlers/GetMigrationJobs.cs
- [ ] T031 GetMigrationExecutionsHandler implementation in Beacon.Core/Features/DataMigration/Handlers/GetMigrationExecutions.cs
- [ ] T032 UpdateMigrationJobHandler implementation in Beacon.Core/Features/DataMigration/Handlers/UpdateMigrationJob.cs
- [ ] T033 DeleteMigrationJobHandler implementation in Beacon.Core/Features/DataMigration/Handlers/DeleteMigrationJob.cs

### Migration Service Layer
- [ ] T034 IMigrationService interface in Beacon.Core/Features/DataMigration/Services/IMigrationService.cs
- [ ] T035 MigrationService implementation extending existing query execution patterns in Beacon.Core/Features/DataMigration/Services/MigrationService.cs
- [ ] T036 MigrationValidationService for query and destination validation in Beacon.Core/Features/DataMigration/Services/MigrationValidationService.cs

## Phase 3.4: UI Components (Sequential - May share component dependencies)

### Supporting Components (Parallel - Different Files)
- [ ] T037 [P] ProjectSelector component in Beacon.UI/Components/Custom/ProjectSelector.razor
- [ ] T038 [P] QueryEditor component in Beacon.UI/Components/Custom/QueryEditor.razor
- [ ] T039 [P] ExecutionStatusChip component in Beacon.UI/Components/Custom/ExecutionStatusChip.razor

### Main UI Components
- [ ] T040 MigrationJobList component in Beacon.UI/Components/Pages/DataMigration/MigrationJobList.razor
- [ ] T041 MigrationJobForm component in Beacon.UI/Components/Pages/DataMigration/MigrationJobForm.razor
- [ ] T042 MigrationExecutionHistory component in Beacon.UI/Components/Pages/DataMigration/MigrationExecutionHistory.razor

### Page Components
- [ ] T043 MigrationListPage in Beacon.UI/Components/Pages/DataMigration/MigrationListPage.razor
- [ ] T044 CreateMigrationJobPage in Beacon.UI/Components/Pages/DataMigration/CreateMigrationJobPage.razor
- [ ] T045 MigrationHistoryPage in Beacon.UI/Components/Pages/DataMigration/MigrationHistoryPage.razor

## Phase 3.5: Integration & Navigation
- [ ] T046 Add Data Migration navigation menu item to MainLayout
- [ ] T047 Register MediatR handlers in DI container
- [ ] T048 Configure routing for migration pages
- [ ] T049 Add migration permissions and authorization
- [ ] T050 Integrate with existing notification system for migration alerts

## Phase 3.6: Polish & Testing

### Unit Tests (Parallel - Different Files)  
- [ ] T051 [P] Unit tests for MigrationJob entity validation in tests/Unit/Entities/MigrationJobTests.cs
- [ ] T052 [P] Unit tests for MigrationExecution entity calculations in tests/Unit/Entities/MigrationExecutionTests.cs
- [ ] T053 [P] Unit tests for MigrationService business logic in tests/Unit/Services/MigrationServiceTests.cs
- [ ] T054 [P] Unit tests for MigrationValidationService in tests/Unit/Services/MigrationValidationServiceTests.cs

### Performance & Final Steps
- [ ] T055 Performance test: Large dataset migration (<200ms per 1000 rows)
- [ ] T056 Run database migration and seed test data
- [ ] T057 Execute quickstart.md scenario end-to-end
- [ ] T058 Update project documentation with migration feature
- [ ] T059 Code review and refactoring cleanup

## Dependencies

**Critical TDD Dependencies:**
- Tests (T004-T017) MUST complete and FAIL before implementation (T018-T050)

**Entity Dependencies:**
- T001 (migration) blocks T018, T019
- T002 (enums) blocks T018, T019  
- T003 (context) blocks T018, T019

**Handler Dependencies:**
- T018-T021 (entities/DTOs) block T022-T027 (request/response)
- T022-T027 (contracts) block T028-T033 (implementations)
- T034-T036 (services) block T029 (ExecuteMigrationJobHandler)

**UI Dependencies:**
- T037-T039 (supporting components) block T040-T042 (main components)
- T040-T042 (components) block T043-T045 (pages)

**Integration Dependencies:**
- T028-T033 (handlers) block T047 (DI registration)
- T043-T045 (pages) block T048 (routing)

## Parallel Execution Examples

### Phase 3.2: Launch All Contract Tests Together
```bash
# All contract tests can run in parallel (different files):
Task: "Contract test CreateMigrationJobHandler in tests/Features/DataMigration/CreateMigrationJobHandlerTests.cs"
Task: "Contract test ExecuteMigrationJobHandler in tests/Features/DataMigration/ExecuteMigrationJobHandlerTests.cs"
Task: "Contract test GetMigrationJobsHandler in tests/Features/DataMigration/GetMigrationJobsHandlerTests.cs"
Task: "Contract test GetMigrationExecutionsHandler in tests/Features/DataMigration/GetMigrationExecutionsHandlerTests.cs"
Task: "Contract test UpdateMigrationJobHandler in tests/Features/DataMigration/UpdateMigrationJobHandlerTests.cs"
Task: "Contract test DeleteMigrationJobHandler in tests/Features/DataMigration/DeleteMigrationJobHandlerTests.cs"
```

### Phase 3.3: Launch Entity Creation Together  
```bash
# Entity models can be created in parallel:
Task: "MigrationJob entity in Beacon.Core/Features/DataMigration/Entities/MigrationJob.cs"
Task: "MigrationExecution entity in Beacon.Core/Features/DataMigration/Entities/MigrationExecution.cs"
Task: "MigrationJobDto record in Beacon.Core/Features/DataMigration/DTOs/MigrationJobDto.cs"
Task: "MigrationExecutionDto record in Beacon.Core/Features/DataMigration/DTOs/MigrationExecutionDto.cs"
```

## Task Generation Rules Applied

1. **From Contracts**: 
   - 6 handler contracts → 6 contract test tasks (T004-T009) [P]
   - 4 UI component contracts → 4 UI test tasks (T010-T013) [P] 
   - 6 handlers → 6 implementation tasks (T028-T033)
   
2. **From Data Model**:
   - 2 entities → 2 model creation tasks (T018-T019) [P]
   - 2 enums → 1 enum creation task (T002) [P]
   - Relationships → service layer tasks (T034-T036)
   
3. **From User Stories**:
   - 4 quickstart scenarios → 4 integration tests (T014-T017) [P]

## Validation Checklist

- [x] All contracts have corresponding tests (T004-T013)
- [x] All entities have model tasks (T018-T021) 
- [x] All tests come before implementation (Phase 3.2 before 3.3)
- [x] Parallel tasks truly independent ([P] marked appropriately)
- [x] Each task specifies exact file path
- [x] No task modifies same file as another [P] task

## Notes
- [P] tasks = different files, no dependencies
- Verify tests fail before implementing (TDD requirement)
- Commit after each task completion
- Follow existing Beacon project patterns and conventions
- Reuse existing query execution infrastructure where possible