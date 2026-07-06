# Tasks: Alerting Tasks Recipient

**Input**: Design documents from `/specs/004-alerting-tasks/`
**Prerequisites**: plan.md, spec.md, data-model.md, contracts/, research.md, quickstart.md

**Tests**: Tests are NOT explicitly requested in the specification. Test tasks are excluded per template guidance.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

**Multi-project solution** (from plan.md):
- **Core**: `src/Beacon.Core/` (entities, handlers, services)
- **UI**: `src/Beacon.UI/` (Blazor components)
- **Providers**: `src/Beacon.Core.PostgreSql/`, `src/Beacon.Core.SqlServer/` (migrations)
- **Sample**: `src/Beacon.SampleProject/` (DI registration)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Enum modification and base entity setup

- [x] T001 [P] Add CreateTasks boolean property to Subscription entity in src/Beacon.Core/Data/Entities/Subscription.cs
- [x] T002 [P] Create AlertingTask entity inheriting from ArchivableBaseEntity in src/Beacon.Core/Data/Entities/AlertingTask.cs
- [x] T003 Configure AlertingTask entity in BeaconContext.OnModelCreating() in src/Beacon.Core/Data/BeaconContext.cs (add DbSet, unique index on SubscriptionId, one-to-many with Notifications)

**Note**: Build solution after T003 to verify entity configuration compiles

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T004 [P] Create TaskData DTO in src/Beacon.Core/DTOs/TaskData.cs
- [x] T005 [P] Create TaskDetailsData DTO in src/Beacon.Core/DTOs/TaskDetailsData.cs
- [x] T006 [P] Create TaskStatisticsData DTO in src/Beacon.Core/DTOs/TaskStatisticsData.cs
- [x] T007 [P] Create ITaskService interface in src/Beacon.Core/Services/ITaskService.cs
- [x] T008 [P] Implement TaskService in src/Beacon.Core/Services/TaskService.cs (find-or-create with auto-resolve)
- [x] T009 Register ITaskService in DI container in src/Beacon.Core/ServiceConfiguration.cs
- [x] T010 Update NotificationService.SendNotification() in src/Beacon.Core/Services/NotificationService.cs (call TaskService directly for Tasks notifications, no adapter needed)

**Migration Note**: User must manually generate migrations after T003:
```bash
dotnet ef migrations add AddTaskEntity --project Beacon.Core.PostgreSql --startup-project Beacon.SampleProject
dotnet ef migrations add AddTaskEntity --project Beacon.Core.SqlServer --startup-project Beacon.SampleProject
dotnet ef database update --project Beacon.Core --startup-project Beacon.SampleProject
```

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - Create Task Recipient and View Generated Tasks (Priority: P1) 🎯 MVP

**Goal**: Users can create Tasks recipients, attach them to subscriptions, and view all generated tasks in a centralized list with basic filtering

**Independent Test**: Create a Tasks recipient via Recipients page, attach to a subscription, trigger query execution, view generated task in Tasks page with filters (resolved/unresolved)

### Implementation for User Story 1

- [x] T013 [P] [US1] Implement GetTasks query method using paged list pattern in TaskService.cs
- [x] T014 [P] [US1] Implement GetTaskDetails query method in TaskService.cs
- [x] T015 [P] [US1] Create Tasks.razor page with MudDataGrid and paging in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor
- [x] T016 [P] [US1] Create TaskDetails.razor page with notification history table in src/Beacon.UI/Components/Pages/Tasks/TaskDetails.razor
- [x] T017 [US1] Add CreateTasks checkbox to AddSubscriptionDialog.razor in src/Beacon.UI/Components/Pages/Subscriptions/AddSubscriptionDialog.razor
- [x] T018 [US1] Add CreateTasks display to SubscriptionDetails.razor in src/Beacon.UI/Components/Pages/Subscriptions/SubscriptionDetails.razor
- [x] T019 [US1] Add CreateTasks property to SubscriptionData and SubscriptionDetailsData DTOs
- [x] T020 [US1] Update SubscriptionService to map CreateTasks in Create/Update/GetDetails methods
- [x] T021 [US1] Add Tasks navigation link to MainLayout.razor in src/Beacon.UI/Components/Layout/MainLayout.razor

**Checkpoint**: At this point, User Story 1 should be fully functional - users can create Tasks recipients, attach to subscriptions, and view generated tasks

**Validation Scenario**:
1. Navigate to Recipients page → Create Tasks recipient (name: "Critical Alerts", no destination)
2. Navigate to Subscriptions page → Attach "Critical Alerts" recipient to subscription
3. Trigger subscription execution (manual or scheduled)
4. Navigate to Tasks page → Verify task appears with subscription name, query name, result count, creation date
5. Filter by "Unresolved" → Verify task appears
6. Click task → Verify details page shows subscription info, execution details

---

## Phase 4: User Story 2 - Resolve and Track Task Progress (Priority: P2)

**Goal**: Users can resolve tasks with optional notes, reopen resolved tasks, and track resolution status

**Independent Test**: Open an existing task, click "Resolve Task", add resolution notes, verify resolved status and timestamp. Reopen task, verify unresolved status.

### Implementation for User Story 2

- [x] T022 [P] [US2] Implement ResolveTask method in TaskService.cs
- [x] T023 [P] [US2] Implement ReopenTask method in TaskService.cs
- [x] T024 [P] [US2] Implement GetTaskStatistics method in TaskService.cs
- [x] T025 [P] [US2] Create ResolveTaskDialog.razor with resolution notes textarea
- [x] T026 [US2] Update TaskDetails.razor with Resolve/Reopen buttons and notification history table
- [x] T027 [US2] Update Notification entity to add TaskId nullable FK in src/Beacon.Core/Data/Entities/Notification.cs

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently - users can create, view, resolve, and reopen tasks

**Validation Scenario**:
1. Navigate to Tasks page → Click unresolved task → Click "Resolve Task"
2. Enter resolution notes: "Data quality issue fixed by updating source system"
3. Submit → Verify task shows "Resolved" badge, resolution timestamp, resolution notes
4. Click "Reopen Task" → Verify task returns to "Unresolved" status
5. Navigate back to Tasks list → Verify task shows "Unresolved" status

---

## Phase 5: User Story 3 - Track Subscription Progress with Task Timeline (Priority: P3)

**Goal**: Users can view a timeline of all tasks generated for a subscription to identify trends and track resolution patterns over time

**Independent Test**: Navigate to subscription details, view "Task History" tab, verify chronological timeline shows all tasks with execution dates, result counts, and resolution status. Generate multiple tasks over time for same subscription, verify timeline shows progression.

### Implementation for User Story 3

- [ ] T024 [P] [US3] Add GetTasksBySubscription method to ITaskService in src/Beacon.Core/Services/ITaskService.cs
- [ ] T025 [P] [US3] Implement GetTasksBySubscription in TaskService.cs in src/Beacon.Core/Services/TaskService.cs (uses GetTasks handler with SubscriptionId filter)
- [ ] T026 [P] [US3] Create TaskTimeline.razor component in src/Beacon.UI/Components/Pages/Tasks/TaskTimeline.razor (timeline visualization with result count trend)
- [ ] T027 [US3] Update SubscriptionDetails.razor in src/Beacon.UI/Components/Pages/Subscriptions/SubscriptionDetails.razor (add "Task History" tab, embed TaskTimeline component)

**Checkpoint**: All user stories (P1, P2, P3) should now be independently functional - users can create, view, resolve, reopen, and track task trends over time

**Validation Scenario**:
1. Navigate to Subscriptions page → Select subscription with Tasks recipient
2. Click "Task History" tab → Verify timeline shows all tasks chronologically
3. Verify each task entry shows: execution date, result count, resolution status
4. Generate 3 tasks over time (different result counts) → Verify timeline shows progression
5. Verify visual indicators for increasing/decreasing trends (if implemented)

---

## Phase 6: User Story 4 - Manage Task Assignments and Notifications (Priority: P4) [OPTIONAL - DEFERRED]

**Goal**: Users can assign tasks to team members and receive notifications when new tasks are created

**Note**: This user story is marked as P4 and may be deferred to a future phase per spec assumptions. Implement only if time allows.

**Independent Test**: Assign a task to a user, verify they receive notification and see it in "Assigned to Me" view.

### Implementation for User Story 4 (DEFERRED)

- [ ] T028 [P] [US4] Add AssignedToUserId nullable field to Task entity in src/Beacon.Core/Data/Entities/Task.cs
- [ ] T029 [P] [US4] Generate migration for Task entity modification (user generates manually)
- [ ] T030 [P] [US4] Implement AssignTask command handler in src/Beacon.Core/Features/Tasks/AssignTask.cs
- [ ] T031 [P] [US4] Create task assignment UI component in src/Beacon.UI/Components/Pages/Tasks/AssignTaskDialog.razor
- [ ] T032 [US4] Update Tasks.razor with "Assigned to Me" filter in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor
- [ ] T033 [US4] Implement notification service integration for task assignments (email/Teams)

**Checkpoint**: Task assignment and notification features complete (if implemented)

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] T034 [P] Implement GetTaskStatistics query handler in src/Beacon.Core/Features/Tasks/GetTaskStatistics.cs
- [ ] T035 Add task statistics dashboard to Tasks.razor page header in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor (total, unresolved, resolved counts)
- [ ] T036 [P] Add client-side sorting to Tasks.razor task list in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor
- [ ] T037 [P] Add pagination to Tasks.razor task list in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor (if >100 tasks)
- [ ] T038 [P] Add error handling and loading states to all Task UI components (Tasks.razor, TaskDetails.razor, ResolveTaskDialog.razor)
- [ ] T039 [P] Add validation for ResolutionNotes max length (2000 chars) in ResolveTaskDialog.razor
- [ ] T040 Build solution with `dotnet build --property WarningLevel=0` and verify no compilation errors
- [ ] T041 Run application with `dotnet watch run --project Beacon.SampleProject` and verify all acceptance scenarios from spec.md
- [ ] T042 [P] Update CLAUDE.md with Tasks feature documentation (if needed)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion (T001-T003) - BLOCKS all user stories
- **User Stories (Phase 3-6)**: All depend on Foundational phase completion (T004-T012)
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3 → P4)
- **Polish (Phase 7)**: Depends on desired user stories being complete (minimally US1+US2)

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after Foundational (Phase 2) - Extends US1 UI but independently testable
- **User Story 3 (P3)**: Can start after Foundational (Phase 2) - Integrates with Subscription details but independently testable
- **User Story 4 (P4)**: Can start after Foundational (Phase 2) - Extends Task entity and US1 UI, independently testable (OPTIONAL/DEFERRED)

### Within Each Phase

**Setup (Phase 1)**:
- T001 and T002 can run in parallel (different files)
- T003 depends on T002 (Task entity must exist before configuring in context)

**Foundational (Phase 2)**:
- T004, T005, T006, T007, T008 can all run in parallel (different files)
- T009 depends on T008 (TasksAdapter must exist before registering)
- T010 and T011 can run in parallel, but T011 depends on T010
- T012 depends on T011 (service must exist before DI registration)

**User Story 1 (Phase 3)**:
- T013, T014, T015, T016 can all run in parallel (different files)
- T017 modifies existing file (no parallelism with other mods to same file)
- T018 modifies different file, can run in parallel with T013-T017

**User Story 2 (Phase 4)**:
- T019, T020, T021 can all run in parallel (different files)
- T022 and T023 modify existing files created in US1 (sequential after US1)

**User Story 3 (Phase 5)**:
- T024, T025, T026 can all run in parallel (different files)
- T027 modifies existing SubscriptionDetails.razor (sequential)

**User Story 4 (Phase 6)**: [OPTIONAL/DEFERRED]
- T028 modifies Task entity (sequential after T002)
- T030, T031 can run in parallel after T029 (different files)
- T032, T033 sequential (modify existing files)

**Polish (Phase 7)**:
- T034, T036, T037, T038, T039, T042 can all run in parallel (different files or independent changes)
- T035 depends on T034 (statistics handler must exist before using in UI)
- T040, T041 are validation steps (sequential at end)

### Parallel Opportunities

- **Setup**: T001 and T002 in parallel
- **Foundational**: T004-T008 all in parallel, then T010-T011 in parallel
- **User Stories**: Once Foundational completes, all user stories can start in parallel (if team capacity allows)
- **Within US1**: T013-T016 in parallel, T018 in parallel with all US1 tasks
- **Within US2**: T019-T021 in parallel
- **Within US3**: T024-T026 in parallel
- **Polish**: T034, T036-T039, T042 all in parallel

---

## Parallel Example: User Story 1

```bash
# Launch all parallel tasks for User Story 1 together:

# Core handlers (can run in parallel):
Task: "Implement GetTasks query handler in src/Beacon.Core/Features/Tasks/GetTasks.cs"
Task: "Implement GetTaskDetails query handler in src/Beacon.Core/Features/Tasks/GetTaskDetails.cs"

# UI components (can run in parallel with handlers):
Task: "Create Tasks.razor page in src/Beacon.UI/Components/Pages/Tasks/Tasks.razor"
Task: "Create TaskDetails.razor page in src/Beacon.UI/Components/Pages/Tasks/TaskDetails.razor"
Task: "Add Tasks navigation link to NavMenu.razor in src/Beacon.UI/Components/Layout/NavMenu.razor"

# Sequential after parallel tasks complete:
Task: "Update AddRecipientDialog.razor to add Tasks option"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

**Goal**: Deliver basic task creation and viewing functionality

1. Complete Phase 1: Setup (T001-T003) + migrations
2. Complete Phase 2: Foundational (T004-T012)
3. Complete Phase 3: User Story 1 (T013-T018)
4. **STOP and VALIDATE**: Test US1 independently using validation scenario
5. Deploy/demo if ready

**Estimated Time**: 4-6 hours
**Deliverable**: Users can create Tasks recipients, attach to subscriptions, view tasks with basic filtering

---

### Incremental Delivery

**Iteration 1: MVP (P1)**
1. Setup + Foundational → Foundation ready (T001-T012)
2. Add User Story 1 → Test independently → Deploy/Demo (T013-T018)
3. **Value Delivered**: Internal notification channel without external integration

**Iteration 2: Workflow Management (P1 + P2)**
1. Add User Story 2 → Test independently → Deploy/Demo (T019-T023)
2. **Value Delivered**: Task resolution tracking and workflow management

**Iteration 3: Analytics (P1 + P2 + P3)**
1. Add User Story 3 → Test independently → Deploy/Demo (T024-T027)
2. **Value Delivered**: Trend analysis and progress tracking over time

**Iteration 4: Collaboration (P1 + P2 + P3 + P4)** [OPTIONAL]
1. Add User Story 4 → Test independently → Deploy/Demo (T028-T033)
2. **Value Delivered**: Team collaboration with assignments and notifications

**Iteration 5: Polish**
1. Add Polish tasks → Final validation (T034-T042)
2. **Value Delivered**: Production-ready feature with statistics, sorting, pagination, error handling

---

### Parallel Team Strategy

**With 2 developers:**

**Phase 1-2** (Together):
- Both developers work on Setup + Foundational (1-2 hours)

**Phase 3+** (Split):
- Developer A: User Story 1 (T013-T018) → 2-3 hours
- Developer B: User Story 2 (T019-T023) → 1-2 hours
- Once A finishes: User Story 3 (T024-T027)
- Once B finishes: Polish tasks (T034-T042)

**Phase 7** (Together):
- Both validate all acceptance scenarios

**Total Time**: 6-8 hours (vs 10-12 hours sequential)

---

## Task Summary

**Total Tasks**: 42 (excluding optional US4)
- **Setup**: 3 tasks (T001-T003)
- **Foundational**: 9 tasks (T004-T012) - CRITICAL BLOCKING PHASE
- **User Story 1** (P1 - MVP): 6 tasks (T013-T018)
- **User Story 2** (P2): 5 tasks (T019-T023)
- **User Story 3** (P3): 4 tasks (T024-T027)
- **User Story 4** (P4 - OPTIONAL): 6 tasks (T028-T033) - DEFERRED
- **Polish**: 9 tasks (T034-T042)

**Parallel Opportunities**: 18 tasks marked [P] can run in parallel (43% parallelizable)

**Independent Test Criteria**:
- ✅ US1: Create Tasks recipient, attach to subscription, view generated task
- ✅ US2: Resolve task with notes, verify status, reopen task
- ✅ US3: View subscription task timeline, verify chronological ordering
- ✅ US4: Assign task to user, verify notification (DEFERRED)

**Suggested MVP Scope**: Phase 1 + Phase 2 + Phase 3 (User Story 1) = 18 tasks, 4-6 hours

**Suggested Production Scope**: MVP + US2 + US3 + Polish = 36 tasks (excluding US4), 10-12 hours

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- **Database migrations must be generated manually by user** after T003 and T029 (per CLAUDE.md guidance)
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- **No test tasks included** - tests not explicitly requested in specification
- User Story 4 (P4) is OPTIONAL/DEFERRED - implement only if time allows
- Follow quickstart.md for detailed implementation guidance for each task
- Validate constitutional compliance: Clean Architecture, CQRS pattern, schema-agnostic migrations
