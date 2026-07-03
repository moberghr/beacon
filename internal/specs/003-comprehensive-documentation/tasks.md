# Tasks: Comprehensive Documentation System

**Input**: Design documents from `/Users/mirkobudimir/Dev/beacon/specs/003-comprehensive-documentation/`
**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, quickstart.md

**Tests**: No tests required for this documentation feature.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3, US4)
- Include exact file paths in descriptions

## Path Conventions

- Documentation files: `docs/` folder at repository root
- Repository root: `/Users/mirkobudimir/Dev/beacon/`
- All paths shown below are relative to repository root

---

## Phase 1: Setup (Jekyll Infrastructure)

**Purpose**: Initialize Jekyll static site and GitHub Pages foundation

- [x] T001 Create docs/ directory structure: docs/{getting-started,features,advanced,api,troubleshooting,contributing}
- [x] T002 Create docs/assets/ directory structure: docs/assets/{css,images/screenshots,images/diagrams,downloads}
- [x] T003 [P] Create docs/_config.yml with Jekyll configuration (site title, theme, navigation, accessibility settings)
- [x] T004 [P] Create docs/Gemfile with Ruby dependencies (Jekyll 4.3, just-the-docs theme, plugins)
- [x] T005 [P] Create docs/assets/css/style.css with WCAG 2.1 Level A accessibility styles
- [ ] T006 Test local Jekyll server: cd docs && bundle install && bundle exec jekyll serve

**Checkpoint**: Jekyll site builds successfully and serves at localhost:4000

---

## Phase 2: Foundational (No Blocking Prerequisites)

**Purpose**: N/A - This is a documentation feature with no shared infrastructure requirements

**Note**: Phase 2 is skipped because documentation pages can be created independently. All user story work can begin after Phase 1 (Setup) is complete.

---

## Phase 3: User Story 1 - Developer Quick Start (Priority: P1) 🎯 MVP

**Goal**: Enable new users to deploy Beacon and create their first notification in under 30 minutes

**Independent Test**: Have a new developer (unfamiliar with Beacon) follow the documentation from scratch and successfully create their first working query with email notification within 30 minutes, without external help.

### Implementation for User Story 1

- [x] T007 [P] [US1] Create README.md with 2-3 sentence overview, key features list, quick start summary, Docker Compose example, badges, and support links
- [x] T008 [P] [US1] Create docs/getting-started/index.md with section overview
- [x] T009 [US1] Create docs/getting-started/installation.md with Docker Compose deployment, environment variables, connection strings for PostgreSQL/SQL Server/MySQL, and API key setup
- [x] T010 [US1] Create docs/getting-started/quick-start.md with step-by-step guide: create project, define query, test execution, create subscription, add recipient, trigger notification
- [x] T011 [P] [US1] Create docs/getting-started/configuration.md with all environment variables, connection string formats, schema configuration, and notification adapters
- [x] T012 [P] [US1] Create docs/features/projects.md with project management guide including purpose, use cases, configuration steps, and examples
- [x] T013 [P] [US1] Create docs/features/queries.md with query creation basics including SQL examples and parameter usage
- [x] T014 [P] [US1] Create docs/features/subscriptions.md with cron scheduling guide and execution frequency examples
- [x] T015 [P] [US1] Create docs/features/notifications.md with email, Teams, and Jira setup instructions and delivery examples

**Checkpoint**: At this point, User Story 1 should be fully functional - a new developer can deploy and create first notification in 30 minutes

---

## Phase 4: User Story 3 - Marketing Site Visitor Conversion (Priority: P3)

**Goal**: Convert visitors to users through compelling value proposition and clear next steps

**Independent Test**: Have someone in a target role (DevOps engineer, DBA, operations manager) visit the marketing site and, within 5 minutes, decide whether they want to try Beacon and understand the next steps.

### Implementation for User Story 3

- [ ] T016 [US3] Create docs/index.md with homepage including hero section, 5 key selling points (multi-database support, flexible alerting, Docker deployment, query chaining, notification channels), and clear CTA to installation guide
- [ ] T017 [US3] Add 3 use case examples to docs/index.md: (1) Database Threshold Alerts with problem/solution/benefits, (2) Cross-Database Reporting with aggregation scenario, (3) Data Migration Orchestration with audit trail benefits
- [ ] T018 [P] [US3] Create sample docker-compose.yml in docs/assets/downloads/ with fully-commented configuration example
- [ ] T019 [US3] Add navigation structure to docs/_config.yml with main sections and hierarchical menu

**Checkpoint**: At this point, User Story 3 should be fully functional - visitors understand value proposition and know how to get started within 5 minutes

---

## Phase 5: User Story 2 - End User Feature Discovery (Priority: P2)

**Goal**: Help users discover all capabilities to evaluate if Beacon can replace their current alerting system

**Independent Test**: Have a potential customer read the feature documentation and accurately list all major capabilities, use cases, and configuration options without watching demos or asking questions.

### Implementation for User Story 2

- [ ] T020 [P] [US2] Create docs/features/index.md with features section overview and comparison of Beacon vs manual SQL monitoring
- [ ] T021 [P] [US2] Create docs/features/multi-step-queries.md with advanced multi-step query guide including result aggregation using @result1, @result2 references
- [ ] T022 [P] [US2] Create docs/features/recipients.md with recipient management guide for email, Teams, and Jira notification targets
- [ ] T023 [P] [US2] Create docs/features/parameters.md with dynamic query parameter guide including placeholder syntax and substitution examples
- [ ] T024 [P] [US2] Create docs/features/data-migration.md with data migration orchestration guide including job creation, execution history, and validation
- [ ] T025 [P] [US2] Create docs/api/index.md with API section overview
- [ ] T026 [US2] Create docs/api/services.md with service interface reference documenting IProjectService, IQueryService, ISubscriptionService, INotificationService, IMigrationService with request/response examples

**Checkpoint**: At this point, User Story 2 should be fully functional - users can discover all features and evaluate Beacon comprehensively

---

## Phase 6: User Story 4 - Advanced User Configuration (Priority: P4)

**Goal**: Support power users with complex scenarios like multi-tenant deployments and query chains

**Independent Test**: Have an advanced user implement a complex scenario (e.g., multi-tenant deployment with custom schemas) using only the documentation, successfully configuring it without trial and error.

### Implementation for User Story 4

- [ ] T027 [P] [US4] Create docs/advanced/index.md with advanced section overview
- [ ] T028 [P] [US4] Create docs/advanced/query-chaining.md with cross-project query chain guide including parameter passing between projects
- [ ] T029 [P] [US4] Create docs/advanced/cross-database.md with multi-database query orchestration guide showing PostgreSQL + SQL Server + MySQL in single workflow
- [ ] T030 [P] [US4] Create docs/advanced/multi-tenant.md with schema-agnostic deployment guide including runtime schema configuration and tenant isolation patterns
- [ ] T031 [P] [US4] Create docs/advanced/custom-providers.md with database provider extension guide showing how to add support for additional databases
- [ ] T032 [P] [US4] Create docs/advanced/architecture.md with Clean Architecture deep-dive explaining layers, dependency flow, and design patterns
- [ ] T033 [P] [US4] Create docs/troubleshooting/index.md with troubleshooting section overview
- [ ] T034 [P] [US4] Create docs/troubleshooting/common-issues.md with FAQ covering connection failures, query timeouts, notification delivery issues, cron validation errors
- [ ] T035 [P] [US4] Create docs/troubleshooting/debugging.md with logging and diagnostics guide including Hangfire job monitoring and query execution history
- [ ] T036 [P] [US4] Create docs/troubleshooting/performance.md with performance tuning guide covering query optimization and notification batching
- [ ] T037 [P] [US4] Create docs/contributing/index.md with contributing section overview
- [ ] T038 [US4] Create docs/contributing/guidelines.md with contribution guide including code style (reference CLAUDE.md), PR process, documentation updates, and testing requirements

**Checkpoint**: At this point, User Story 4 should be fully functional - power users can implement complex scenarios independently

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Visual assets, code validation, accessibility testing, and final quality checks

### Screenshots

- [ ] T039 [P] Capture screenshot of Beacon dashboard (Home.razor) and save to docs/assets/images/screenshots/dashboard.png with descriptive alt text
- [ ] T040 [P] Capture screenshot of project creation dialog and save to docs/assets/images/screenshots/project-create.png
- [ ] T041 [P] Capture screenshot of query builder interface and save to docs/assets/images/screenshots/query-builder.png
- [ ] T042 [P] Capture screenshot of multi-step query configuration and save to docs/assets/images/screenshots/multi-step-query.png
- [ ] T043 [P] Capture screenshot of subscription configuration with cron expression and save to docs/assets/images/screenshots/subscription-config.png
- [ ] T044 [P] Capture screenshot of notification history view and save to docs/assets/images/screenshots/notification-history.png
- [ ] T045 [P] Capture screenshot of data migration job creation and save to docs/assets/images/screenshots/migration-job.png

### Diagrams

- [ ] T046 [P] Create architecture overview diagram showing Clean Architecture layers and save to docs/assets/images/diagrams/architecture.svg
- [ ] T047 [P] Create multi-step query execution flow diagram and save to docs/assets/images/diagrams/query-execution-flow.svg
- [ ] T048 [P] Create cross-database query orchestration diagram and save to docs/assets/images/diagrams/cross-database.svg
- [ ] T049 [P] Create schema-agnostic deployment topology diagram and save to docs/assets/images/diagrams/multi-tenant.svg
- [ ] T050 [P] Create notification delivery workflow diagram and save to docs/assets/images/diagrams/notification-workflow.svg

### Documentation Integration

- [ ] T051 Embed screenshots into relevant documentation pages with captions and alt text (installation.md, features/*.md, advanced/*.md)
- [ ] T052 Embed diagrams into relevant documentation pages (architecture.md, multi-step-queries.md, cross-database.md, multi-tenant.md)
- [ ] T053 Add navigation breadcrumbs and "Related Documentation" links to all pages

### Code Example Validation

- [ ] T054 Extract all Docker Compose examples from documentation and validate they work
- [ ] T055 Extract all SQL query examples and validate syntax
- [ ] T056 Extract all C# service usage examples and validate they follow CLAUDE.md style guide
- [ ] T057 Extract all connection string examples and validate format for PostgreSQL, SQL Server, MySQL
- [ ] T058 Verify all code examples include explanatory comments

### Quality Validation

- [ ] T059 Run pa11y accessibility tests on all documentation pages: cd docs && bundle exec jekyll serve && pa11y http://localhost:4000
- [ ] T060 Verify all images have descriptive alt text (not just "screenshot of X")
- [ ] T061 Verify color contrast meets 4.5:1 minimum ratio using browser DevTools
- [ ] T062 Test keyboard navigation on all pages (Tab, Shift+Tab, Enter)
- [ ] T063 Run linkchecker to verify no broken internal or external links: linkchecker http://localhost:4000
- [ ] T064 Verify all pages load in under 2 seconds on local server
- [ ] T065 Test mobile responsiveness on small (375px), medium (768px), and large (1440px) viewport sizes
- [ ] T066 Verify consistent terminology usage across all pages (Query not "SQL Query", Subscription not "Schedule", etc.)

### Final Documentation Review

- [ ] T067 Verify README.md includes badges (build status, version, license, documentation)
- [ ] T068 Verify all documentation pages start with clear goal statement and prerequisites
- [ ] T069 Verify quick start guide is completable in under 30 minutes (SC-001)
- [ ] T070 Verify 90% of common questions answerable within 2 minutes using navigation (SC-002)
- [ ] T071 Verify documentation covers 100% of features from constitution and codebase (SC-006)
- [ ] T072 Verify all code examples execute successfully without modification (SC-007)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **User Stories (Phase 3-6)**: All depend on Setup completion, but can proceed in parallel once Setup is done
  - Recommended order: P1 (US1) → P3 (US3) → P2 (US2) → P4 (US4)
  - Can parallelize if multiple writers available
- **Polish (Phase 7)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Setup - No dependencies on other stories
- **User Story 3 (P3)**: Can start after Setup - Depends on US1 for CTA links to installation guide
- **User Story 2 (P2)**: Can start after Setup - Independent of other stories
- **User Story 4 (P4)**: Can start after Setup - Independent of other stories (references advanced concepts but doesn't block)

### Within Each User Story

- Most documentation pages within a story can be created in parallel (marked with [P])
- Homepage (T016) should be created after installation.md (T009) exists for CTA links
- Navigation structure (T019) should be updated after section index pages exist

### Parallel Opportunities

- All Setup tasks can run sequentially (configuration dependencies)
- Within User Story 1: T007, T008, T011, T012, T013, T014, T015 can all run in parallel
- Within User Story 3: T018 can run in parallel with T016, T017
- Within User Story 2: T020-T024 and T025 can all run in parallel, T026 depends on T025
- Within User Story 4: T027-T032 and T033-T036 and T037 can all run in parallel, T038 depends on T037
- All screenshot tasks (T039-T045) can run in parallel
- All diagram tasks (T046-T050) can run in parallel

---

## Parallel Example: User Story 1

```bash
# Launch all P1 core documentation in parallel:
- T007 [P] [US1] README.md
- T008 [P] [US1] docs/getting-started/index.md
- T011 [P] [US1] docs/getting-started/configuration.md
- T012 [P] [US1] docs/features/projects.md
- T013 [P] [US1] docs/features/queries.md
- T014 [P] [US1] docs/features/subscriptions.md
- T015 [P] [US1] docs/features/notifications.md

# Then sequentially (dependencies):
- T009 [US1] docs/getting-started/installation.md
- T010 [US1] docs/getting-started/quick-start.md (depends on T009)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (Jekyll infrastructure)
2. Complete Phase 3: User Story 1 (Developer Quick Start)
3. **STOP and VALIDATE**: Test User Story 1 independently
   - Have unfamiliar developer follow quick start
   - Verify 30-minute completion time
   - Check all links work
   - Validate code examples
4. Deploy to GitHub Pages (merge to main branch)
5. Monitor success metrics (SC-001, SC-002)

### Incremental Delivery

1. Complete Setup (Phase 1) → Jekyll site functional
2. Add User Story 1 (Phase 3) → Test independently → Deploy (MVP!)
   - **Value**: New users can onboard in 30 minutes
3. Add User Story 3 (Phase 4) → Test independently → Deploy
   - **Value**: Marketing site converts visitors to users
4. Add User Story 2 (Phase 5) → Test independently → Deploy
   - **Value**: Users discover all capabilities
5. Add User Story 4 (Phase 6) → Test independently → Deploy
   - **Value**: Power users can implement complex scenarios
6. Add Polish (Phase 7) → Final validation → Deploy
   - **Value**: Visual assets, accessibility, quality assurance

### Parallel Team Strategy

With multiple documentation writers:

1. Team completes Setup together (Phase 1)
2. Once Setup is done:
   - **Writer A**: User Story 1 (P1) - Critical path
   - **Writer B**: User Story 3 (P3) - Marketing
   - **Writer C**: User Story 2 (P2) - Feature discovery
   - **Writer D**: User Story 4 (P4) - Advanced topics
3. Stories complete and integrate independently
4. Team collaborates on Phase 7 (Polish)

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label (US1, US2, US3, US4) maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Commit after each task or logical group (e.g., all files for one feature)
- Stop at any checkpoint to validate story independently
- No tests required - this is documentation content creation
- All code examples must be validated before considering task complete
- Screenshots should be captured last (after content stable) to avoid outdated images

---

## Task Count Summary

- **Phase 1 (Setup)**: 6 tasks
- **Phase 3 (US1 - P1)**: 9 tasks
- **Phase 4 (US3 - P3)**: 4 tasks
- **Phase 5 (US2 - P2)**: 7 tasks
- **Phase 6 (US4 - P4)**: 12 tasks
- **Phase 7 (Polish)**: 34 tasks
- **Total**: 72 tasks

**Parallel Opportunities**: 59 tasks marked with [P] can be executed in parallel (with proper phase dependencies)

**MVP Scope**: Phase 1 (6 tasks) + Phase 3 (9 tasks) = 15 tasks for minimal viable documentation
