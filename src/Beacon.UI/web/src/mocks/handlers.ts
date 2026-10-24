/**
 * Browser dev-mode MSW handlers — run the full UI with NO .NET backend:
 *
 *     npm run dev:mock        (= VITE_MOCK_API=1 vite)
 *
 * Notes:
 * - These handlers are for LOCAL DEV ONLY. They are dynamically imported in
 *   main.tsx behind an `import.meta.env.DEV` guard (tree-shaken out of prod
 *   bundles) and the vite.config.ts build plugin deletes mockServiceWorker.js
 *   from the build output — nothing mock-related ever ships.
 * - Fixtures must stay OBVIOUSLY fake ("Demo Analytics", "Sample Warehouse").
 *   §9.1 ("no fake/seed/demo data") applies to real UI pages populating from
 *   real sources — not to this dev tooling.
 * - Unhandled /beacon/api/* GETs return a 404 problem JSON (instead of a
 *   network error against the missing backend) so secondary pages render
 *   their empty/error states cleanly. Unhandled mutations return a 400
 *   problem JSON ("Mock mode: mutation not mocked").
 * - SignalR: the /beacon/api/hub negotiate POST is stubbed to 503 so the hub
 *   fails fast with a single warning instead of retry spam. Real-time events
 *   simply don't fire in mock mode.
 *
 * Test handlers live in src/test/handlers.ts and are intentionally separate.
 *
 * Coverage (GET unless noted):
 * - csrf, auth/me, auth/logout (POST)
 * - home: trends, activity, migration-summary, task-summary, uptime
 * - projects, projects/:id, projects/:id/documentation, projects/:id/members, projects/:id/member-candidates,
 *   PUT/DELETE projects/:id/members/:userId, PUT projects/:id/access, POST projects/:id/data-sources,
 *   DELETE projects/:id/data-sources/:dataSourceId (no state kept)
 * - queries, query-folders, queries/:id, queries/:id/versions, query-versions/:id
 * - data-sources, data-sources/:id/metadata, POST data-sources/test-connection, PUT data-sources/:id,
 *   PUT data-sources/:id/enabled (no state kept)
 * - notifications (filters by ?subscriptionId), notifications/:id
 * - tasks, tasks/:id (+ executions / related / result-history / comments)
 * - approvals/pending, approvals/:id, approvals/changes, approvals/changes/:id (31 by kind, 41 a data source's
 *   connection, 51 the LLM connection)
 * - subscriptions, subscriptions/:id, subscriptions/:id/anomaly-chart
 * - recipients
 * - ai-actors, ai-actors/:id, ai-actors/:id/pending-plans, ai-actors/pending-plans/count, ai-actors/plans/:id
 *   (+ approve / reject / request-revision)
 * - mcp: settings, tools, learning-stats, learned-patterns, documentation-patches
 * - eval: golden/candidates, golden, grounding/recheck (honest empties)
 * - control-tower: statistics, health, subscriptions/:id/detail
 * - migrations: jobs, executions (honest empties)
 * - admin: api-keys, users, users/roles, PUT users/:id/roles, POST users/:id/sign-out-everywhere (no state kept),
 *   users/:id/owned-resources, security-audit, POST security-audit/verify (honest empties), admin-settings,
 *   admin-settings/llm-providers, user-settings
 * - data-quality: overview, contracts
 * - catch-alls: 404 GET / 400 mutations / 503 hub negotiate
 */
import { http, HttpResponse } from 'msw';
import {
  AiActorActionType,
  AiActorExecutionPhase,
  AiActorPlanStatus,
  AiActorStatus,
  AiProvider,
  AnomalyDetectionMethod,
  AnomalySensitivity,
  ApprovalStatus,
  ChangeApprovalKind,
  BedrockAuthMode,
  ChangeSource,
  DatabaseEngineType,
  DataSourceType,
  FileType,
  HealthStatus,
  McpDocPatchStatus,
  McpPatternStatus,
  McpPatternType,
  NotificationStatus,
  NotificationTrigger,
  NotificationType,
  ParameterType,
  ProjectMembershipManagement,
  ProjectRole,
  QueryVersionStatus,
  TaskPriority,
} from '@/lib/enums';

const now = new Date();
const iso = (daysAgo: number, hours = 0) =>
  new Date(now.getTime() - daysAgo * 86_400_000 - hours * 3_600_000).toISOString();

const problem = (status: number, title: string, detail: string) =>
  HttpResponse.json(
    { type: 'about:blank', title, status, detail },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );

// ---------- auth / csrf -----------------------------------------------------

const authHandlers = [
  // csrf.ts only needs the XSRF-TOKEN cookie to exist after this GET.
  http.get('/beacon/api/csrf', () =>
    new HttpResponse(null, {
      status: 204,
      headers: { 'Set-Cookie': 'XSRF-TOKEN=mock-csrf-token; Path=/' },
    })),
  http.get('/beacon/api/auth/me', () =>
    HttpResponse.json({
      userId: 'mock-admin',
      userName: 'mock.admin',
      displayName: 'Mock Admin',
      email: 'mock.admin@example.test',
      isAuthenticated: true,
      roles: ['Admin'],
      realtimeEnabled: true,
      signOutEverywhereEnabled: true,
    })),
  // An Admin holds every functional permission.
  http.get('/beacon/api/auth/permissions', () =>
    HttpResponse.json({
      canRead: true,
      canWrite: true,
      isAdmin: true,
      permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'UserAdmin', 'DataSourceAdmin', 'MigrationOperator'],
      migrationsEnabled: true,
    })),
  http.get('/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false, localLoginEnabled: true })),
  http.post('/beacon/api/auth/logout', () => new HttpResponse(null, { status: 204 })),
];

// ---------- home -------------------------------------------------------------

const spark = [3, 5, 4, 7, 6, 8, 9];
const trend30 = Array.from({ length: 30 }, (_, i) => 40 + ((i * 7) % 25));

const homeHandlers = [
  http.get('/beacon/api/home/trends', () =>
    HttpResponse.json({
      totalSubscriptions: 12,
      subscriptionDelta: 2,
      queryExecutions30d: 1480,
      queryExecutionsDelta: 96,
      notificationsSent30d: 312,
      notificationsDelta: -14,
      anomaliesOpen: 3,
      anomaliesAcknowledged: 9,
      anomaliesDelta: 1,
      avgExecutionMs: 412,
      avgExecutionDeltaPct: -6.2,
      fastestQueryName: 'Demo: Daily Order Count',
      fastestQueryMs: 38,
      fastestQueryDeltaMs: -4,
      slowestQueryName: 'Demo: Cross-Source Revenue Join',
      slowestQueryMs: 2840,
      slowestQueryDeltaMs: 120,
      dataSourcesOnline: 3,
      recipientsCount: 5,
      integrationsCount: 2,
      subscriptionsSpark: spark,
      queriesSpark: spark.map((x) => x * 3),
      notificationsSpark: spark.map((x) => x * 2),
      anomaliesSpark: [0, 1, 0, 2, 1, 0, 1],
      queryTrend30d: trend30,
      notificationsTrend30d: trend30.map((x) => Math.round(x / 3)),
      perfBuckets: [
        { label: 'Demo Warehouse', avgMs: 320, p50Ms: 210, p95Ms: 900, p99Ms: 1900 },
        { label: 'Sample CRM', avgMs: 540, p50Ms: 400, p95Ms: 1400, p99Ms: 2800 },
      ],
    })),
  http.get('/beacon/api/home/activity', () =>
    HttpResponse.json({
      items: [
        { tone: 'ok', icon: 'check', title: 'Demo: Daily Order Count executed', meta: '42 rows', timestamp: iso(0, 1) },
        { tone: 'info', icon: 'bell', title: 'Notification sent to Demo Ops Team', meta: 'Sample Warehouse', timestamp: iso(0, 3) },
        { tone: 'warn', icon: 'alert', title: 'Anomaly detected on Demo Revenue Watch', meta: '+38% vs baseline', timestamp: iso(1) },
        { tone: 'ok', icon: 'database', title: 'Sample Warehouse metadata refreshed', meta: '128 tables', timestamp: iso(2) },
      ],
    })),
  http.get('/beacon/api/home/migration-summary', () =>
    HttpResponse.json({ total: 4, successful: 18, executions: 20, errored: 2 })),
  http.get('/beacon/api/home/task-summary', () =>
    HttpResponse.json({ total: 9, open: 3, resolved: 6 })),
  http.get('/beacon/api/home/uptime', () =>
    HttpResponse.json({
      ticks: Array.from({ length: 24 }, (_, i) => (i === 7 ? 'warn' : i === 16 ? 'crit' : 'ok')),
    })),
];

// ---------- projects ---------------------------------------------------------

const projectsHandlers = [
  http.get('/beacon/api/projects', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          name: 'Demo Analytics',
          description: 'Mock project — not real data',
          dataSourceCount: 2,
          repositoryCount: 1,
          qualityScore: 87,
          lastScanStatus: 'Completed',
          lastScanAt: iso(1),
          createdAt: iso(40),
          requiresMembership: false,
          myRole: ProjectRole.ProjectAdmin,
          memberCount: 1,
        },
        {
          id: 2,
          name: 'Sample Warehouse',
          description: null,
          dataSourceCount: 1,
          repositoryCount: 0,
          qualityScore: null,
          lastScanStatus: null,
          lastScanAt: null,
          createdAt: iso(12),
          requiresMembership: true,
          myRole: ProjectRole.ProjectAdmin,
          memberCount: 1,
        },
      ]))),
  http.get('/beacon/api/projects/:id', ({ params }) =>
    HttpResponse.json({
      project: {
        id: Number(params.id),
        name: Number(params.id) === 2 ? 'Sample Warehouse' : 'Demo Analytics',
        description: 'Mock project — not real data',
        totalTables: 128,
        qualityScore: 87,
        codeReferenceCount: 342,
        lastScanAt: iso(1),
        dataSources: [
          { id: 1, name: 'Demo Warehouse (PostgreSQL)', type: 'Database', tableCount: 96, qualityScore: 91 },
          { id: 2, name: 'Sample CRM (SQL Server)', type: 'Database', tableCount: 32, qualityScore: 78 },
        ],
        repositories: [
          {
            id: 1,
            name: 'demo-analytics-repo',
            url: 'https://example.test/demo/demo-analytics-repo',
            scanStatus: 'Completed',
            lastScanAt: iso(1),
            referenceCount: 342,
            hasAccessToken: false,
          },
        ],
        hasDocumentation: false,
        // The mock user is an Admin: project admin everywhere, and Beacon manages membership.
        requiresMembership: Number(params.id) === 2,
        myRole: ProjectRole.ProjectAdmin,
        canManageMembers: true,
        // Sample CRM is also linked to the open Demo Analytics project.
        openlyReachableDataSourceIds: Number(params.id) === 2 ? [2] : [],
        sharedSourceProjects: sharedSourceProjects(Number(params.id)),
        sharedSourceGuardrails: sharedSourceGuardrails(Number(params.id)),
        membershipManagement: ProjectMembershipManagement.Beacon,
      },
    })),
  http.post('/beacon/api/projects', () => HttpResponse.json({ projectId: 2, requiresMembership: true }, { status: 201 })),
  http.get('/beacon/api/projects/:id/documentation', () =>
    HttpResponse.json({ latest: null, history: [] })),
  http.get('/beacon/api/projects/:id/members', () =>
    HttpResponse.json(paged([
      {
        userId: 1,
        displayName: 'Mock Admin',
        role: ProjectRole.ProjectAdmin,
        isActive: true,
        addedAt: iso(40),
        modifiedAt: null,
        // The mock user is an Admin: every membership is theirs to change.
        canChange: true,
      },
    ]))),
  http.get('/beacon/api/projects/:id/member-candidates', ({ request }) => {
    const search = (new URL(request.url).searchParams.get('search') ?? '').toLowerCase();
    const people = [
      { userId: 1, displayName: 'Mock Admin', isMember: true, canAdd: true },
      { userId: 2, displayName: 'Sample Analyst', isMember: false, canAdd: true },
    ];
    return HttpResponse.json({ candidates: people.filter(x => x.displayName.toLowerCase().includes(search)) });
  }),
  http.put('/beacon/api/projects/:id/members/:userId', async ({ params, request }) => {
    const body = (await request.json()) as { role: ProjectRole };
    return HttpResponse.json({ projectId: Number(params.id), userId: Number(params.userId), role: body.role, added: true });
  }),
  http.delete('/beacon/api/projects/:id/members/:userId', () => new HttpResponse(null, { status: 204 })),
  http.put('/beacon/api/projects/:id/access', async ({ params, request }) => {
    const body = (await request.json()) as { requiresMembership: boolean };
    return HttpResponse.json({
      projectId: Number(params.id),
      requiresMembership: body.requiresMembership,
      sharedSourceProjects: sharedSourceProjects(Number(params.id)),
    });
  }),
  http.post('/beacon/api/projects/:id/data-sources', async ({ params, request }) => {
    const body = (await request.json()) as { dataSourceIds: number[] };
    return HttpResponse.json({ projectId: Number(params.id), linkedDataSourceIds: body.dataSourceIds });
  }),
  http.delete('/beacon/api/projects/:id/data-sources/:dataSourceId', ({ params }) =>
    HttpResponse.json({ projectId: Number(params.id), dataSourceId: Number(params.dataSourceId), leftUnlinked: false })),
];

/** Sample CRM's guardrails: the stricter of the two mock projects' settings, and which project sets each. */
function sharedSourceGuardrails(projectId: number) {
  const other = projectId === 2 ? { projectId: 1, projectName: 'Demo Analytics' } : { projectId: 2, projectName: 'Sample Warehouse' };
  const own = projectId === 2 ? { projectId: 2, projectName: 'Sample Warehouse' } : { projectId: 1, projectName: 'Demo Analytics' };
  return [
    {
      dataSourceId: 2,
      dataSourceName: 'Sample CRM (SQL Server)',
      guardrails: [
        { setting: 'enforceReadOnly', value: 'true', ...own },
        { setting: 'enablePiiDetection', value: 'true', ...other },
        { setting: 'maxRowLimit', value: '500', ...other },
        { setting: 'statementTimeoutSeconds', value: '30', ...own },
        { setting: 'maxResultBytes', value: '10485760', ...own },
        { setting: 'maxExplainCost', value: 'none', ...own },
        { setting: 'customPiiPatterns', value: '0', ...own },
      ],
    },
  ];
}

/** The two mock projects share Sample CRM (data source 2); Sample Warehouse is the restricted one. */
function sharedSourceProjects(projectId: number) {
  return projectId === 2
    ? [{ projectId: 1, name: 'Demo Analytics', requiresMembership: false, sharedDataSourceIds: [2], projectAdminNames: ['Mock Admin'] }]
    : [{ projectId: 2, name: 'Sample Warehouse', requiresMembership: true, sharedDataSourceIds: [2], projectAdminNames: [] }];
}

// ---------- queries ----------------------------------------------------------

/** Wraps fixture rows in the paged list wire shape every list endpoint returns. */
function paged<T>(items: T[]) {
  return { items, totalCount: items.length, pageCount: items.length === 0 ? 0 : 1 };
}

const queriesHandlers = [
  http.get('/beacon/api/queries', () =>
    HttpResponse.json({
      items: [
        {
          queryId: 1,
          name: 'Demo: Daily Order Count',
          description: 'Mock query against Sample Warehouse',
          createdTime: iso(30),
          subscriptionsCount: 2,
          folderId: null,
          folderPath: null,
          aiActorId: null,
          aiActorName: null,
          steps: [{ stepId: 1, stepOrder: 1, name: 'Count orders', dataSourceName: 'Demo Warehouse (PostgreSQL)' }],
        },
        {
          queryId: 2,
          name: 'Demo: Cross-Source Revenue Join',
          description: null,
          createdTime: iso(14),
          subscriptionsCount: 1,
          folderId: null,
          folderPath: null,
          aiActorId: null,
          aiActorName: null,
          steps: [
            { stepId: 2, stepOrder: 1, name: 'Warehouse revenue', dataSourceName: 'Demo Warehouse (PostgreSQL)' },
            { stepId: 3, stepOrder: 2, name: 'CRM accounts', dataSourceName: 'Sample CRM (SQL Server)' },
          ],
        },
      ],
      totalCount: 2,
      pageCount: 1,
    })),
  http.get('/beacon/api/query-folders', () => HttpResponse.json({ folders: [] })),
  // Detail for the two list fixtures above (QueryDetailsData, returned unwrapped).
  http.get('/beacon/api/queries/:id', ({ params }) => {
    const id = Number(params.id);
    const crossSource = id === 2;
    const steps = [
      {
        stepId: crossSource ? 2 : 1,
        stepOrder: 1,
        name: crossSource ? 'Warehouse revenue' : 'Count orders',
        description: null,
        sqlValue: crossSource
          ? 'SELECT account_id, SUM(amount) AS revenue\nFROM demo.orders\nGROUP BY account_id'
          : 'SELECT COUNT(*) AS order_count\nFROM demo.orders\nWHERE created_at >= {startDate}',
        dataSourceId: 1,
        dataSourceName: 'Demo Warehouse (PostgreSQL)',
        dataSourceType: DataSourceType.Database,
        databaseEngineType: DatabaseEngineType.PostgreSQL,
        databaseEngineDescription: 'PostgreSQL',
        parameters: crossSource
          ? []
          : [{ name: 'startDate', type: ParameterType.DateTime, description: 'Mock parameter', placeholder: '{startDate}' }],
      },
      ...(crossSource
        ? [{
            stepId: 3,
            stepOrder: 2,
            name: 'CRM accounts',
            description: null,
            sqlValue: 'SELECT id, name FROM dbo.accounts',
            dataSourceId: 2,
            dataSourceName: 'Sample CRM (SQL Server)',
            dataSourceType: DataSourceType.Database,
            databaseEngineType: DatabaseEngineType.MSSQL,
            databaseEngineDescription: 'SQL Server',
            parameters: [],
          }]
        : []),
    ];
    return HttpResponse.json({
      id,
      name: crossSource ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
      description: crossSource ? null : 'Mock query against Sample Warehouse',
      createdTime: iso(crossSource ? 14 : 30),
      totalExecutions: crossSource ? 40 : 120,
      sentNotifications: crossSource ? 12 : 64,
      steps,
      finalQuery: crossSource ? 'SELECT a.name, r.revenue FROM step1 r JOIN step2 a ON a.id = r.account_id' : null,
      finalQueryDataSourceId: null,
      aiActorId: null,
      aiActorName: null,
      isLocked: false,
      subscriptions: [
        {
          subscriptionId: id,
          createdTime: iso(20),
          name: 'Demo daily digest',
          subscribers: 'Demo Ops Team',
          cronExpression: '0 7 * * *',
        },
      ],
      notificationHistory: [7, 5, 3, 1].map(d => ({
        date: iso(d),
        totalExecutions: 4,
        successfulNotifications: d === 3 ? 3 : 4,
        failedExecutions: d === 3 ? 1 : 0,
        successRate: d === 3 ? 75 : 100,
      })),
      avgExecutionTimeMs: crossSource ? 1980 : 412,
      minExecutionTimeMs: crossSource ? 850 : 38,
      maxExecutionTimeMs: crossSource ? 2840 : 980,
      executionTimeHistory: [7, 5, 3, 1].map(d => ({
        date: iso(d),
        avgExecutionTimeMs: (crossSource ? 1900 : 400) + d * 10,
        minExecutionTimeMs: crossSource ? 850 : 38,
        maxExecutionTimeMs: crossSource ? 2840 : 980,
      })),
      isMultiStep: crossSource,
      isCrossDataSource: crossSource,
      isCrossDatabase: crossSource,
      dataSourceNames: crossSource
        ? ['Demo Warehouse (PostgreSQL)', 'Sample CRM (SQL Server)']
        : ['Demo Warehouse (PostgreSQL)'],
    });
  }),
  // Versions tab (QueryVersionSummary[], returned unwrapped).
  // Paged preview: honours ?page, ?pageSize and ?sort over a 137-row fixture, like the server's paging.
  http.post('/beacon/api/queries/:id/preview', ({ request }) => {
    const url = new URL(request.url);
    const page = Number(url.searchParams.get('page') ?? 0);
    const pageSize = Number(url.searchParams.get('pageSize') ?? 20);
    const sort = url.searchParams.get('sort');
    const rows = Array.from({ length: 137 }, (_, index) => ({
      order_id: 9001 + index,
      status: ['shipped', 'pending', 'returned'][index % 3],
      amount: Math.round(((index * 37) % 500) * 1.5 * 100) / 100,
    }));
    if (sort) {
      const column = sort.replace(/^-/, '') as keyof (typeof rows)[number];
      const direction = sort.startsWith('-') ? -1 : 1;
      rows.sort((a, b) => (a[column] > b[column] ? 1 : a[column] < b[column] ? -1 : 0) * direction);
    }
    return HttpResponse.json({
      success: true,
      errorMessage: null,
      totalExecutionTimeMs: 412,
      dataSourcesInvolved: ['Demo Warehouse'],
      steps: [{ stepOrder: 1, stepName: 'Step 1', dataSourceName: 'Demo Warehouse', databaseEngine: 'PostgreSQL', success: true, errorMessage: null, executionTimeMs: 412, totalRows: rows.length, previewRows: [] }],
      result: {
        rows: rows.slice(page * pageSize, page * pageSize + pageSize),
        totalCount: rows.length,
        pageCount: Math.ceil(rows.length / pageSize),
        page,
        pageSize,
        sortable: true,
        sort,
      },
    });
  }),
  http.get('/beacon/api/queries/:id/versions', ({ params }) =>
    HttpResponse.json(paged([
      {
        id: 102,
        versionNumber: 2,
        label: 'Mock v2',
        status: 1,
        name: Number(params.id) === 2 ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
        createdTime: iso(3),
        createdByUserName: 'Mock Admin',
        changeSource: 'User',
        changeReason: 'Mock change — widened date window',
        stepCount: Number(params.id) === 2 ? 2 : 1,
      },
      {
        id: 101,
        versionNumber: 1,
        label: null,
        status: 2,
        name: Number(params.id) === 2 ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
        createdTime: iso(20),
        createdByUserName: 'Mock Admin',
        changeSource: 'User',
        changeReason: null,
        stepCount: 1,
      },
    ]))),
  // Version detail (QueryVersionDetail with step snapshots, unwrapped).
  http.get('/beacon/api/query-versions/:id', ({ params }) =>
    HttpResponse.json({
      id: Number(params.id),
      versionNumber: Number(params.id) === 101 ? 1 : 2,
      label: Number(params.id) === 101 ? null : 'Mock v2',
      status: Number(params.id) === 101 ? 2 : 1,
      name: 'Demo: Daily Order Count',
      description: 'Mock query against Sample Warehouse',
      finalQuery: null,
      createdTime: iso(Number(params.id) === 101 ? 20 : 3),
      createdByUserName: 'Mock Admin',
      changeSource: 'User',
      changeReason: Number(params.id) === 101 ? null : 'Mock change — widened date window',
      steps: [
        {
          stepOrder: 1,
          sqlValue: 'SELECT COUNT(*) AS order_count\nFROM demo.orders\nWHERE created_at >= {startDate}',
          dataSourceId: 1,
          dataSourceName: 'Demo Warehouse (PostgreSQL)',
          name: 'Count orders',
          description: null,
        },
      ],
    })),
];

// ---------- data sources -----------------------------------------------------

const dataSourceEntries = [
        {
          id: 1,
          name: 'Demo Warehouse (PostgreSQL)',
          dataSourceType: 'Database',
          databaseEngineType: 'PostgreSQL',
          queryCount: 2,
          migrationJobsCount: 0,
          metadataLoadingEnabled: true,
          openWhenUnlinked: true,
          projects: [{ id: 1, name: 'Demo Analytics' }],
          // A project the mock user may not see: shown as "+1 other".
          otherProjectCount: 1,
          databaseReadOnlyEnforced: true,
          allowMigrationWrites: false,
          transportVerified: true,
          // The by-id read's stored settings, which the Replace connection dialog sends back unchanged.
          editSettings: { hostManaged: false, metadataMaxTables: 0, metadataMaxColumnsPerTable: 0, metadataLoadTableNamesOnly: false },
        },
        {
          id: 2,
          name: 'Sample CRM (SQL Server)',
          dataSourceType: 'Database',
          databaseEngineType: 'MSSQL',
          queryCount: 1,
          migrationJobsCount: 0,
          metadataLoadingEnabled: false,
          openWhenUnlinked: true,
          projects: [{ id: 1, name: 'Demo Analytics' }, { id: 2, name: 'Sample Warehouse' }],
          otherProjectCount: 0,
          // SQL Server does not refuse writes in a read-only session: the page warns.
          databaseReadOnlyEnforced: false,
          allowMigrationWrites: false,
          // Its connection does not make the driver verify the server: badged "Transport not verified".
          transportVerified: false,
          editSettings: { hostManaged: false, metadataMaxTables: 0, metadataMaxColumnsPerTable: 0, metadataLoadTableNamesOnly: false },
          // A change of its connection waits for approval (change request 41): badged "Awaiting approval".
          approval: { requestId: 41, kind: ChangeApprovalKind.DataSourceConnection, requestedTime: iso(0, 2), awaitsFirstApproval: false },
        },
        {
          id: 3,
          name: 'Reporting (migration write target)',
          dataSourceType: 'Database',
          databaseEngineType: 'MSSQL',
          queryCount: 0,
          migrationJobsCount: 0,
          metadataLoadingEnabled: true,
          openWhenUnlinked: false,
          projects: [],
          otherProjectCount: 0,
          databaseReadOnlyEnforced: false,
          // Migrations write to it and Beacon never reads it: labelled as a write target, not asked for a read-only login.
          allowMigrationWrites: true,
        },
      ];

// The pending connection changes the mock approvals list and detail serve: a data source's (41, the one the data-source
// list badges) and the LLM connection's (51).
const mockConnectionChanges = [
  {
    id: 41,
    kind: ChangeApprovalKind.DataSourceConnection,
    resourceId: 2,
    resourceName: 'Sample CRM (SQL Server)',
    resourceExists: true,
    status: ApprovalStatus.Pending,
    requestedByUserName: 'Mock Operator',
    reviewedByUserName: null,
    reviewedAt: null,
    reviewComment: null,
    changeSummary: 'Host, Database changed',
    createdTime: iso(0, 2),
    previousMigrationJob: null,
    proposedMigrationJob: null,
    previousCustomSqlRules: null,
    proposedCustomSqlRules: null,
    previousRecipientDestination: null,
    proposedRecipientDestination: null,
    previousDataSourceConnection: {
      name: 'Sample CRM (SQL Server)',
      dataSourceType: DataSourceType.Database,
      databaseEngineType: DatabaseEngineType.MSSQL,
      host: 'crm-db.mock.internal',
      port: 1433,
      database: 'crm',
      login: 'beacon_reader',
      changedSettings: [],
      secretOnlyDefinition: false,
      fingerprint: '3f1a9c0b72de',
      authenticatesServer: false,
      databaseReadOnlyEnforced: false,
      isTargetChange: false,
    },
    proposedDataSourceConnection: {
      name: 'Sample CRM (SQL Server)',
      dataSourceType: DataSourceType.Database,
      databaseEngineType: DatabaseEngineType.MSSQL,
      host: 'crm-replica.mock.internal',
      port: 1433,
      database: 'crm_reporting',
      login: 'beacon_reader',
      changedSettings: ['Data Source', 'Encrypt', 'Initial Catalog'],
      secretOnlyDefinition: false,
      fingerprint: '8be04d1c5a97',
      authenticatesServer: true,
      databaseReadOnlyEnforced: false,
      isTargetChange: true,
    },
    previousLlmConnection: null,
    proposedLlmConnection: null,
    currentProjects: [{ id: 1, name: 'Demo Analytics' }, { id: 2, name: 'Sample Warehouse' }],
    payloadHash: 'mock-connection-payload-hash',
    isOwnRequest: false,
    mayApprove: true,
    approveBlockedReason: null,
    isStale: false,
    changeHiddenByMask: false,
    commentRequiredToApprove: false,
  },
  {
    id: 51,
    kind: ChangeApprovalKind.LlmConnection,
    resourceId: 0,
    resourceName: 'LLM connection',
    resourceExists: true,
    status: ApprovalStatus.Pending,
    requestedByUserName: 'Mock Operator',
    reviewedByUserName: null,
    reviewedAt: null,
    reviewComment: null,
    changeSummary: 'Provider, Endpoint changed',
    createdTime: iso(0, 1),
    previousMigrationJob: null,
    proposedMigrationJob: null,
    previousCustomSqlRules: null,
    proposedCustomSqlRules: null,
    previousRecipientDestination: null,
    proposedRecipientDestination: null,
    previousDataSourceConnection: null,
    proposedDataSourceConnection: null,
    previousLlmConnection: {
      fromHostConfiguration: false,
      provider: AiProvider.OpenAI,
      endpoint: null,
      endpointFingerprint: null,
      region: null,
      bedrockAuthMode: BedrockAuthMode.IamRole,
      model: 'gpt-4o',
      fastModel: 'gpt-4o-mini',
      basedOn: null,
      credentials: [{ name: 'API key', fingerprint: '5d0c2e9a41b7' }],
    },
    proposedLlmConnection: {
      fromHostConfiguration: false,
      provider: AiProvider.AzureOpenAI,
      endpoint: 'https://mock-openai.openai.azure.com',
      endpointFingerprint: 'c41e7f20d98a',
      region: null,
      bedrockAuthMode: BedrockAuthMode.IamRole,
      model: 'gpt-4o',
      fastModel: 'gpt-4o-mini',
      basedOn: '9a7b3c1d0e2f',
      credentials: [{ name: 'API key', fingerprint: '5d0c2e9a41b7' }],
    },
    currentProjects: [],
    payloadHash: 'mock-llm-payload-hash',
    isOwnRequest: false,
    mayApprove: true,
    approveBlockedReason: null,
    isStale: false,
    changeHiddenByMask: false,
    commentRequiredToApprove: false,
  },
];

const dataSourcesHandlers = [
  http.get('/beacon/api/data-sources/:id', ({ params }) => {
    const entry = dataSourceEntries.find(x => x.id === Number(params.id));
    return entry ? HttpResponse.json(entry) : new HttpResponse(null, { status: 404 });
  }),
  http.get('/beacon/api/data-sources', ({ request }) => {
    const targets = new URL(request.url).searchParams.get('migrationTargets');
    return HttpResponse.json(paged(dataSourceEntries.filter(
      x => targets === null || String(x.allowMigrationWrites === true) === targets,
    )));
  }),
  http.put('/beacon/api/data-sources/:id/migration-writes', () => new HttpResponse(null, { status: 204 })),
  http.post('/beacon/api/data-sources/test-connection', () =>
    HttpResponse.json({ success: true, message: 'Connection successful.' })),
  http.put('/beacon/api/data-sources/:id/enabled', () => new HttpResponse(null, { status: 204 })),
  http.put('/beacon/api/data-sources/:id', () => new HttpResponse(null, { status: 204 })),
  // Schema explorer on the detail page (DatabaseMetadataSnapshot, unwrapped).
  http.get('/beacon/api/data-sources/:id/metadata', ({ params }) => {
    const id = Number(params.id);
    const col = (
      columnName: string,
      dataType: string,
      extra: Partial<Record<string, unknown>> = {},
    ) => ({
      columnName,
      dataType,
      isNullable: false,
      isPrimaryKey: false,
      isForeignKey: false,
      ordinalPosition: 1,
      foreignKeyTable: null,
      foreignKeyColumn: null,
      defaultValue: null,
      maxLength: null,
      description: null,
      ...extra,
    });
    return HttpResponse.json({
      dataSourceId: id,
      databaseEngineType: id === 2 ? 'MSSQL' : 'PostgreSQL',
      tables: [
        {
          schemaName: id === 2 ? 'dbo' : 'demo',
          tableName: id === 2 ? 'accounts' : 'orders',
          columns: [
            col('id', id === 2 ? 'int' : 'integer', { isPrimaryKey: true }),
            col(id === 2 ? 'name' : 'account_id', id === 2 ? 'nvarchar' : 'integer', {
              ordinalPosition: 2,
              maxLength: id === 2 ? 200 : null,
            }),
            col(id === 2 ? 'created_at' : 'amount', id === 2 ? 'datetime2' : 'numeric', {
              ordinalPosition: 3,
              isNullable: true,
              description: 'Mock column',
            }),
          ],
          indexes: [
            {
              indexName: id === 2 ? 'pk_accounts' : 'pk_orders',
              isUnique: true,
              isPrimaryKey: true,
              columns: ['id'],
            },
          ],
          description: 'Mock table — not real data',
        },
      ],
      refreshedAt: iso(1),
    });
  }),
];

// ---------- notifications / tasks / approvals / subscriptions ----------------

const notificationEntries = [
        {
          id: 1,
          subscriptionId: 1,
          queryName: 'Demo: Daily Order Count',
          status: NotificationStatus.NotificationSent,
          resultCount: 42,
          executionTimeMs: 230,
          createdTime: iso(0, 2),
          aiActorId: null,
          aiActorName: null,
          comment: null,
          recipientNames: ['Demo Ops Team'],
        },
        {
          id: 2,
          subscriptionId: 2,
          queryName: 'Demo: Cross-Source Revenue Join',
          status: NotificationStatus.NoResults,
          resultCount: 0,
          executionTimeMs: 1840,
          createdTime: iso(1),
          aiActorId: null,
          aiActorName: null,
          comment: null,
          recipientNames: ['Sample Finance Group'],
        },
];

const listHandlers = [
  // Serves both the notifications page and the subscription executions tab
  // (which calls GET /notifications?subscriptionId=N).
  http.get('/beacon/api/notifications', ({ request }) => {
    const subscriptionId = new URL(request.url).searchParams.get('subscriptionId');
    const entries = subscriptionId
      ? notificationEntries.filter(x => x.subscriptionId === Number(subscriptionId))
      : notificationEntries;
    return HttpResponse.json(paged(entries));
  }),
  // NotificationDetailPage (wrapped in { entry }).
  http.get('/beacon/api/notifications/:id', ({ params }) => {
    const id = Number(params.id);
    const crossSource = id === 2;
    return HttpResponse.json({
      entry: {
        id,
        queryId: crossSource ? 2 : 1,
        queryName: crossSource ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
        subscriptionId: crossSource ? 2 : 1,
        recipientName: crossSource ? 'Sample Finance Group' : 'Demo Ops Team',
        type: crossSource ? NotificationType.Slack : NotificationType.Email,
        status: crossSource ? NotificationStatus.NoResults : NotificationStatus.NotificationSent,
        createdTime: iso(crossSource ? 1 : 0, 2),
        sentAt: iso(crossSource ? 1 : 0, 2),
        executionTimeMs: crossSource ? 1840 : 230,
        resultCount: crossSource ? 0 : 42,
        results: crossSource
          ? null
          : JSON.stringify([
              { order_id: 9001, status: 'shipped', amount: 120.5 },
              { order_id: 9002, status: 'pending', amount: 89.0 },
            ]),
      },
    });
  }),
  http.get('/beacon/api/tasks', () =>
    HttpResponse.json({
      items: [
        {
          id: 1,
          subscriptionName: 'Demo: orders watch',
          queryName: 'Demo: Daily Order Count',
          latestResultCount: 42,
          notificationCount: 3,
          executionCount: 12,
          uniqueResultCounts: 4,
          createdAt: iso(2),
          resolved: false,
          resolvedAt: null,
          resolvedByUserName: null,
          aiActorId: null,
          aiActorName: null,
        },
        {
          id: 2,
          subscriptionName: 'Demo: revenue watch',
          queryName: 'Demo: Cross-Source Revenue Join',
          latestResultCount: 7,
          notificationCount: 1,
          executionCount: 5,
          uniqueResultCounts: 2,
          createdAt: iso(6),
          resolved: true,
          resolvedAt: iso(4),
          resolvedByUserName: 'Mock Admin',
          aiActorId: null,
          aiActorName: null,
        },
      ],
      totalCount: 2,
      pageCount: 1,
    })),
  // TaskDetailPage fires all five of these on mount (TaskDetail is unwrapped).
  http.get('/beacon/api/tasks/:id', ({ params }) =>
    HttpResponse.json({
      id: Number(params.id),
      queryId: 1,
      queryName: 'Demo: Daily Order Count',
      subscriptionId: 1,
      subscriptionName: 'Demo: orders watch',
      subscriptionDescription: 'Mock subscription — not real data',
      latestResultCount: 42,
      notificationCount: 3,
      lastNotificationAt: iso(0, 2),
      createdAt: iso(2),
      resolved: false,
      resolvedAt: null,
      resolvedByUserName: null,
      resolutionNotes: null,
      aiActorId: null,
      aiActorName: null,
      lastExecutionAt: iso(0, 2),
      cronExpression: '0 7 * * *',
      priority: TaskPriority.Normal,
      hasAssignee: false,
      assigneeUserName: null,
      assignedToCaller: false,
      snoozedUntil: null,
      slaHours: 24,
      watcherCount: 1,
      isWatching: false,
      hasOwner: false,
      ownerDisplayName: null,
    })),
  http.get('/beacon/api/tasks/:id/executions', ({ params }) =>
    HttpResponse.json({
      taskId: Number(params.id),
      executions: [
        { id: 901, executedAt: iso(0, 2), durationMs: 412, rowCount: 42, status: 'Sent' },
        { id: 900, executedAt: iso(1), durationMs: 388, rowCount: 40, status: 'Sent' },
        { id: 899, executedAt: iso(2), durationMs: 5012, rowCount: 0, status: 'Failed' },
      ],
    })),
  http.get('/beacon/api/tasks/:id/related', ({ params }) =>
    HttpResponse.json({
      taskId: Number(params.id),
      related: [
        { id: 30, createdAt: iso(9), latestResultCount: 38, resolved: true, resolvedAt: iso(7) },
      ],
    })),
  http.get('/beacon/api/tasks/:id/result-history', ({ params }) =>
    HttpResponse.json({
      taskId: Number(params.id),
      points: [7, 5, 3, 2, 1, 0].map(d => ({
        sampledAt: iso(d),
        resultCount: 36 + d,
      })),
    })),
  http.get('/beacon/api/tasks/:id/comments', ({ params }) =>
    HttpResponse.json({
      taskId: Number(params.id),
      comments: [
        {
          id: 1,
          content: 'Mock comment: looked into the spike, appears seasonal.',
          userName: 'Mock Admin',
          createdAt: iso(1),
        },
      ],
    })),
  http.get('/beacon/api/approvals/pending', () =>
    HttpResponse.json(paged([
      {
        id: 1,
        queryId: 2,
        queryName: 'Demo: Cross-Source Revenue Join',
        versionNumber: 3,
        status: ApprovalStatus.Pending,
        requestedByUserName: 'Mock Analyst',
        createdTime: iso(0, 5),
        changeSummary: `Mock change (source: ${ChangeSource.User}) — widened revenue window`,
      },
    ]))),
  // Changes of migration jobs, Custom SQL rules, recipients, data-source connections and the LLM connection. Keep BEFORE
  // /approvals/:id, which would match /approvals/changes too.
  http.get('/beacon/api/approvals/changes', ({ request }) => {
    const kind = Number(new URL(request.url).searchParams.get('kind') ?? ChangeApprovalKind.MigrationJob);
    const connection = mockConnectionChanges.find(x => x.kind === kind);
    return HttpResponse.json(paged([
      connection
        ? {
            id: connection.id,
            kind,
            resourceId: connection.resourceId,
            resourceName: connection.resourceName,
            status: ApprovalStatus.Pending,
            requestedByUserName: connection.requestedByUserName,
            createdTime: connection.createdTime,
            changeSummary: connection.changeSummary,
          }
        : {
            id: 31,
            kind,
            resourceId: 4,
            resourceName: kind === ChangeApprovalKind.RecipientDestination ? 'Mock ops webhook' : 'Mock nightly copy',
            status: ApprovalStatus.Pending,
            requestedByUserName: 'Mock Operator',
            createdTime: iso(0, 3),
            changeSummary: kind === ChangeApprovalKind.MigrationJob ? 'Migration job created' : 'Mock change',
          },
    ]));
  }),
  http.get('/beacon/api/approvals/changes/:id', ({ params }) => {
    const connection = mockConnectionChanges.find(x => x.id === Number(params.id));
    return connection ? HttpResponse.json(connection) : HttpResponse.json({
      id: Number(params.id),
      kind: ChangeApprovalKind.MigrationJob,
      resourceId: 4,
      resourceName: 'Mock nightly copy',
      resourceExists: true,
      status: ApprovalStatus.Pending,
      requestedByUserName: 'Mock Operator',
      reviewedByUserName: null,
      reviewedAt: null,
      reviewComment: null,
      changeSummary: 'Migration job created',
      createdTime: iso(0, 3),
      previousMigrationJob: null,
      proposedMigrationJob: {
        sourceDataSourceId: 1,
        sourceDataSourceName: 'Mock warehouse',
        queryText: 'SELECT id, total FROM orders',
        destinationDataSourceId: 2,
        destinationDataSourceName: 'Mock reporting',
        destinationTable: 'orders_copy',
        mode: 1,
        transformationScript: null,
      },
      previousCustomSqlRules: null,
      proposedCustomSqlRules: null,
      previousRecipientDestination: null,
      proposedRecipientDestination: null,
      payloadHash: 'mock-payload-hash',
      isOwnRequest: false,
      mayApprove: true,
      approveBlockedReason: null,
      isStale: false,
    });
  }),
  // ApprovalDetailPage (ApprovalRequestDetail, unwrapped). Keep AFTER the
  // /approvals/pending handler — both match two path segments.
  http.get('/beacon/api/approvals/:id', ({ params }) => {
    const proposedVersion = {
      id: 203,
      versionNumber: 3,
      label: null,
      status: QueryVersionStatus.PendingApproval,
      name: 'Demo: Cross-Source Revenue Join',
      description: 'Mock query — widened revenue window',
      finalQuery: 'SELECT a.name, r.revenue FROM step1 r JOIN step2 a ON a.id = r.account_id WHERE r.revenue > 0',
      createdTime: iso(0, 5),
      createdByUserName: 'Mock Analyst',
      changeSource: 'User',
      changeReason: 'Mock change — widened revenue window',
    };
    const currentActiveVersion = {
      id: 202,
      versionNumber: 2,
      label: 'Mock v2',
      status: QueryVersionStatus.Active,
      name: 'Demo: Cross-Source Revenue Join',
      description: null,
      finalQuery: 'SELECT a.name, r.revenue FROM step1 r JOIN step2 a ON a.id = r.account_id',
      createdTime: iso(14),
      createdByUserName: 'Mock Admin',
      changeSource: 'User',
      changeReason: null,
    };
    return HttpResponse.json({
      id: Number(params.id),
      queryId: 2,
      queryName: 'Demo: Cross-Source Revenue Join',
      queryVersionId: 203,
      status: ApprovalStatus.Pending,
      requestedByUserId: 'mock-analyst',
      requestedByUserName: 'Mock Analyst',
      reviewedByUserName: null,
      reviewedAt: null,
      reviewComment: null,
      changeSummary: 'Mock change — widened revenue window',
      createdTime: iso(0, 5),
      proposedVersion,
      currentActiveVersion,
      autoDiff: {
        versionA: currentActiveVersion,
        versionB: proposedVersion,
        nameChanged: false,
        descriptionChanged: true,
        finalQueryChanged: true,
      },
    });
  }),
  http.get('/beacon/api/subscriptions', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          queryId: 1,
          queryName: 'Demo: Daily Order Count',
          cronExpression: '0 7 * * *',
          recipientCount: 1,
          recipientNames: ['Demo Ops Team'],
          aiActorId: null,
          aiActorName: null,
          createTasks: true,
          storeResults: true,
        },
        {
          id: 2,
          queryId: 2,
          queryName: 'Demo: Cross-Source Revenue Join',
          cronExpression: '0 */6 * * *',
          recipientCount: 1,
          recipientNames: ['Sample Finance Group'],
          aiActorId: null,
          aiActorName: null,
          createTasks: false,
          storeResults: false,
        },
      ]))),
  // SubscriptionDetailPage (wrapped in { detail }). The executions tab reuses
  // GET /notifications?subscriptionId=N above; anomaly chart is below.
  http.get('/beacon/api/subscriptions/:id', ({ params }) => {
    const id = Number(params.id);
    const crossSource = id === 2;
    return HttpResponse.json({
      detail: {
        id,
        queryId: id,
        queryName: crossSource ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
        status: 'Active',
        cronExpression: crossSource ? '0 */6 * * *' : '0 7 * * *',
        cronDescription: crossSource ? 'Every 6 hours' : 'At 07:00, every day',
        cronNextAt: iso(-1),
        aiActorId: null,
        aiActorName: null,
        hasOwner: true,
        ownerDisplayName: crossSource ? 'Mock Analyst' : 'Mock Admin',
        ownerNotAuthorized: crossSource,
        notApproved: false,
        maxRows: 100,
        minimumRowCount: null,
        includeAttachment: !crossSource,
        resultAttachmentType: crossSource ? null : FileType.Csv,
        showQuery: false,
        timeoutSeconds: 120,
        storeResults: !crossSource,
        createTasks: !crossSource,
        notificationTrigger: crossSource
          ? NotificationTrigger.Always
          : NotificationTrigger.OnResultCountChange,
        parameters: crossSource
          ? []
          : [{ queryPlaceholder: '{startDate}', value: '2026-01-01' }],
        recipients: [
          crossSource
            ? {
                id: 2,
                name: 'Sample Finance Group',
                description: 'Mock recipient — not real',
                destination: null,
                notificationType: NotificationType.Slack,
              }
            : {
                id: 1,
                name: 'Demo Ops Team',
                description: 'Mock recipient — not real',
                destination: null,
                notificationType: NotificationType.Email,
              },
        ],
        anomalyConfig: crossSource
          ? null
          : {
              enabled: true,
              detectionMethod: AnomalyDetectionMethod.StandardDeviation,
              sensitivity: AnomalySensitivity.Medium,
              lookbackDays: 30,
              alertOnIncrease: true,
              alertOnDecrease: false,
              minimumDataPoints: 7,
            },
      },
    });
  }),
  http.put('/beacon/api/subscriptions/:id/owner', () => new HttpResponse(null, { status: 204 })),
  // Anomaly tab (AnomalyChartResult, unwrapped).
  http.get('/beacon/api/subscriptions/:id/anomaly-chart', ({ params }) => {
    const hasAnomalyDetection = Number(params.id) !== 2;
    return HttpResponse.json({
      hasAnomalyDetection,
      points: hasAnomalyDetection
        ? [9, 8, 7, 6, 5, 4, 3, 2, 1, 0].map(d => ({
            dateTime: iso(d),
            resultCount: d === 3 ? 138 : 40 + d,
            isAnomaly: d === 3,
            notificationSent: d === 3,
            anomalySeverity: d === 3 ? 'High' : null,
            queryExecutionHistoryId: 890 + d,
          }))
        : [],
      baselineMean: hasAnomalyDetection ? 44 : null,
      upperThreshold: hasAnomalyDetection ? 70 : null,
      lowerThreshold: hasAnomalyDetection ? 18 : null,
    });
  }),
  // Recipients tab on the subscription detail page + /recipients list page.
  http.get('/beacon/api/recipients', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          name: 'Demo Ops Team',
          description: 'Mock recipient — not real',
          destination: 'demo.ops@example.test',
          notificationType: NotificationType.Email,
          headersJson: null,
          bodyTemplate: null,
          secretsUnreadable: false,
          subscriptionCount: 1,
        },
        {
          id: 2,
          name: 'Sample Finance Group',
          description: 'Mock recipient — not real',
          // Masked as the server returns it to admins.
          destination: 'https://hooks.slack.com/********',
          notificationType: NotificationType.Slack,
          headersJson: null,
          bodyTemplate: null,
          secretsUnreadable: false,
          subscriptionCount: 1,
        },
      ]))),
];

// ---------- ai actors ----------------------------------------------------------

const aiActorsHandlers = [
  http.get('/beacon/api/ai-actors', () =>
    HttpResponse.json(paged([
        {
          actorId: 1,
          name: 'Demo Revenue Watcher',
          instructions: 'Mock actor — watch demo revenue for anomalies.',
          dataSourceId: 1,
          dataSourceName: 'Demo Warehouse (PostgreSQL)',
          status: AiActorStatus.Active,
          thinkCount: 4,
          lastThinkTime: iso(1),
          totalCost: 0.42,
          createdTime: iso(15),
        },
      ]))),
  http.get('/beacon/api/ai-actors/:id', ({ params }) =>
    HttpResponse.json({
      actorId: Number(params.id),
      name: 'Demo Revenue Watcher',
      instructions: 'Mock actor — watch demo revenue for anomalies.',
      additionalContext: null,
      dataSourceId: 1,
      dataSourceName: 'Demo Warehouse (PostgreSQL)',
      status: AiActorStatus.Active,
      maxQueries: 3,
      maxSubscriptionsPerQuery: 2,
      requiresApproval: true,
      totalTokensUsed: 18_400,
      totalCost: 0.42,
      lastThinkTime: iso(1),
      thinkCount: 4,
      lastError: null,
      createdTime: iso(15),
      pendingPlanCount: 0,
      hasOwner: true,
      ownerDisplayName: 'Mock Admin',
      recentExecutions: [
        {
          executionId: 41,
          phase: AiActorExecutionPhase.Skipped,
          startedAt: iso(0, 3),
          completedAt: iso(0, 3),
          queriesCreated: 0,
          subscriptionsCreated: 0,
          tokensUsed: 0,
          estimatedCost: 0,
          decisionSummary: null,
          errorMessage: 'Skipped: owner not authorized.',
        },
        {
          executionId: 40,
          phase: AiActorExecutionPhase.Completed,
          startedAt: iso(1),
          completedAt: iso(1),
          queriesCreated: 1,
          subscriptionsCreated: 1,
          tokensUsed: 4_600,
          estimatedCost: 0.11,
          decisionSummary: 'Mock cycle: added a revenue drop watch.',
          errorMessage: null,
        },
      ],
    })),
  http.get('/beacon/api/ai-actors/pending-plans/count', () => HttpResponse.json({ count: 1 })),
  http.get('/beacon/api/ai-actors/:id/pending-plans', ({ params }) =>
    HttpResponse.json({
      plans: [
        {
          planId: 7,
          actorId: Number(params.id),
          actorName: 'Demo Revenue Watcher',
          userInstruction: null,
          analysis: 'Mock plan — demo revenue dipped twice this week; tighten the watch.',
          actionCount: 1,
          proposedAt: iso(0, 2),
          expiresAt: iso(-7),
          version: 2,
          tokensUsed: 3_100,
          estimatedCost: 0.07,
        },
      ],
    })),
  http.get('/beacon/api/ai-actors/plans/:id', ({ params }) =>
    HttpResponse.json({
      planId: Number(params.id),
      actorId: 1,
      actorName: 'Demo Revenue Watcher',
      status: Number(params.id) === 7 ? AiActorPlanStatus.PendingApproval : AiActorPlanStatus.RevisionRequested,
      userInstruction: null,
      analysis: 'Mock plan — demo revenue dipped twice this week; tighten the watch.',
      findings: ['Mock finding — two dips below the weekly average.'],
      proposedActions: [
        {
          actionType: AiActorActionType.RefineQuery,
          reasoning: 'Mock reasoning — compare with the weekly average instead of a fixed floor.',
          parameters: { queryId: 12 },
          currentSql: 'SELECT sum(amount) FROM demo_orders WHERE amount < 100',
          proposedSql: 'SELECT sum(amount) FROM demo_orders WHERE amount < (SELECT avg(amount) FROM demo_orders)',
          isLocked: false,
          targetQueryName: 'Demo revenue floor',
          targetQueryId: 12,
        },
      ],
      proposedAt: iso(0, 2),
      expiresAt: iso(-7),
      isExpired: false,
      reviewedAt: Number(params.id) === 7 ? null : iso(1),
      reviewerComment: Number(params.id) === 7 ? null : 'Mock comment — use the weekly average.',
      executedAt: null,
      executionId: null,
      tokensUsed: 3_100,
      estimatedCost: 0.07,
      model: null,
      version: Number(params.id) === 7 ? 2 : 1,
      parentPlanId: Number(params.id) === 7 ? 6 : null,
      mayApprove: Number(params.id) === 7,
      approveBlockedReason: null,
      mayRejectOrRevise: Number(params.id) === 7,
    })),
  http.post('/beacon/api/ai-actors/plans/:id/approve', () => HttpResponse.json({ success: true })),
  http.post('/beacon/api/ai-actors/plans/:id/reject', ({ params }) => HttpResponse.json({ success: true, planId: Number(params.id) })),
  http.post('/beacon/api/ai-actors/plans/:id/request-revision', ({ params }) =>
    HttpResponse.json({ success: true, originalPlanId: Number(params.id) })),
];

// ---------- mcp -----------------------------------------------------------------

const mcpHandlers = [
  // The default deployment locks read-only enforcement and PII detection on.
  http.get('/beacon/api/mcp/settings/locks', () =>
    HttpResponse.json({
      lockedFields: ['EnablePiiDetection', 'EnforceReadOnly'],
      locks: [
        { fieldName: 'EnablePiiDetection', lockName: 'ForcePiiDetection' },
        { fieldName: 'EnforceReadOnly', lockName: 'ForceReadOnly' },
      ],
    })),
  http.get('/beacon/api/mcp/settings', () =>
    HttpResponse.json({
      askSystemPrompt: null,
      globalInstruction: 'Mock instruction — demo environment only.',
      getContextDescription: null,
      queryDescription: null,
      getDocumentationDescription: null,
      askDescription: null,
      searchDescription: null,
      maxRowLimit: 1000,
      enforceReadOnly: true,
      enablePiiDetection: true,
      customPiiPatterns: [],
      enableSampleValueCollection: true,
      enableLearning: true,
      learningAutoApproveThreshold: 0.9,
      learningInjectionBudgetChars: 4000,
      learningSignalRetentionDays: 90,
    })),
  http.get('/beacon/api/mcp/tools', () =>
    HttpResponse.json({
      toolNames: ['get_context', 'query', 'get_documentation', 'ask', 'search'],
    })),
  http.get('/beacon/api/mcp/learning-stats', () =>
    HttpResponse.json({
      totalSignals: 84,
      signals7d: 12,
      signals30d: 51,
      successRate: 0.925,
      patternsApproved: 3,
      patternsPending: 1,
      patternsRejected: 1,
      patchesApplied: 2,
      patchesProposed: 1,
      problemTables: [
        { tablesUsed: 'demo.orders', totalQueries: 30, errorCount: 4, errorRate: 0.133 },
      ],
    })),
  http.get('/beacon/api/mcp/learned-patterns', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          projectId: 1,
          dataSourceId: 1,
          schemaName: 'demo',
          tableName: 'orders',
          columnName: 'status',
          patternType: McpPatternType.ColumnClarification,
          patternContent: 'Mock pattern: status column holds shipped/pending/cancelled.',
          exampleQuestion: 'How many shipped orders this week?',
          exampleSql: "SELECT COUNT(*) FROM demo.orders WHERE status = 'shipped'",
          signalCount: 6,
          confidence: 0.93,
          status: McpPatternStatus.Pending,
          createdTime: iso(4),
          lastRefreshedAt: iso(1),
          reviewedByName: null,
          reviewedAt: null,
          // The mock user is an Admin: every pattern is theirs to review.
          canCurate: true,
        },
      ]))),
  http.get('/beacon/api/mcp/documentation-patches', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          projectId: 1,
          dataSourceId: 1,
          targetType: 0,
          targetIdentifier: 'demo.orders.status',
          currentContent: null,
          proposedContent: 'Mock patch: enum-like status column (shipped/pending/cancelled).',
          reasoning: 'Mock reasoning — repeated clarification signals.',
          supportingSignalCount: 6,
          status: McpDocPatchStatus.Proposed,
          createdTime: iso(3),
          appliedAt: null,
          appliedByName: null,
          canCurate: true,
        },
      ]))),
  http.get('/beacon/api/eval/golden/candidates', () => HttpResponse.json(paged([]))),
  http.get('/beacon/api/eval/golden', () => HttpResponse.json([])),
  http.get('/beacon/api/eval/grounding/recheck', ({ request }) =>
    HttpResponse.json({
      projectId: Number(new URL(request.url).searchParams.get('projectId')),
      goldenCasesChecked: 0,
      patternsChecked: 0,
      refused: [],
    })),
];

// ---------- control tower ----------------------------------------------------

const controlTowerHandlers = [
  http.get('/beacon/api/control-tower/statistics', () =>
    HttpResponse.json({
      statistics: {
        totalSubscriptions: 2,
        healthySubscriptions: 1,
        warningSubscriptions: 1,
        criticalSubscriptions: 0,
        stalledSubscriptions: 0,
        totalUnresolvedTasks: 1,
        totalAnomalies30Days: 3,
        overallSuccessRate: 96.4,
        timeRangeDays: 30,
        ownerNotAuthorizedContracts: 0,
      },
    })),
  http.get('/beacon/api/control-tower/health', () =>
    HttpResponse.json({
      items: [
        {
          subscriptionId: 1,
          queryName: 'Demo: Daily Order Count',
          dataSourceName: 'Demo Warehouse (PostgreSQL)',
          folderPath: null,
          healthStatus: HealthStatus.Green,
          totalExecutions: 120,
          successfulExecutions: 118,
          failedExecutions: 2,
          successRate: 98.3,
          lastExecutionTime: iso(0, 2),
          lastExecutionStatus: NotificationStatus.NotificationSent,
          lastResultCount: 42,
          ownerNotAuthorized: false,
          notApproved: false,
          unresolvedTaskCount: 0,
          totalTaskCount: 3,
          anomalyCount30Days: 1,
          anomalySparkline: [{ date: iso(3), anomalyCount: 1 }],
          isActive: true,
          createTasks: true,
          storeResults: true,
          hasAnomalyDetection: true,
          aiActorId: null,
          aiActorName: null,
        },
        {
          subscriptionId: 2,
          queryName: 'Demo: Cross-Source Revenue Join',
          dataSourceName: 'Sample CRM (SQL Server)',
          folderPath: null,
          healthStatus: HealthStatus.Amber,
          totalExecutions: 40,
          successfulExecutions: 36,
          failedExecutions: 4,
          successRate: 90.0,
          lastExecutionTime: iso(1),
          lastExecutionStatus: NotificationStatus.Skipped,
          lastResultCount: 0,
          ownerNotAuthorized: true,
          notApproved: false,
          unresolvedTaskCount: 1,
          totalTaskCount: 2,
          anomalyCount30Days: 2,
          anomalySparkline: [
            { date: iso(8), anomalyCount: 1 },
            { date: iso(2), anomalyCount: 1 },
          ],
          isActive: true,
          createTasks: false,
          storeResults: false,
          hasAnomalyDetection: false,
          aiActorId: null,
          aiActorName: null,
        },
      ],
      totalCount: 2,
      pageCount: 1,
    })),
  http.get('/beacon/api/control-tower/subscriptions/:id/detail', ({ params }) =>
    HttpResponse.json({
      detail: {
        subscriptionId: Number(params.id),
        queryName: Number(params.id) === 2 ? 'Demo: Cross-Source Revenue Join' : 'Demo: Daily Order Count',
        queryId: Number(params.id),
        folderPath: null,
        cronExpression: '0 7 * * *',
        timeRangeDays: 30,
        recentExecutions: [
          {
            executionId: 902,
            createdTime: iso(0, 1),
            notificationStatus: NotificationStatus.Skipped,
            resultCount: 0,
            executionTimeMs: 0,
            errorMessage: 'Skipped: owner not authorized.',
          },
          {
            executionId: 901,
            createdTime: iso(0, 2),
            notificationStatus: NotificationStatus.NotificationSent,
            resultCount: 42,
            executionTimeMs: 412,
            errorMessage: null,
          },
          {
            executionId: 900,
            createdTime: iso(1),
            notificationStatus: NotificationStatus.NoResults,
            resultCount: 0,
            executionTimeMs: 388,
            errorMessage: null,
          },
          {
            executionId: 899,
            createdTime: iso(2),
            notificationStatus: NotificationStatus.Failed,
            resultCount: 0,
            executionTimeMs: 5012,
            errorMessage: 'Mock error: connection timeout to Demo Warehouse.',
          },
        ],
        openTasks: [
          {
            taskId: 31,
            createdTime: iso(1),
            snoozedUntil: null,
            latestResultCount: 42,
            priority: 2,
            assigneeUserId: null,
          },
        ],
        recentAnomalies: [
          {
            anomalyId: 11,
            detectedTime: iso(1),
            severity: 'High',
            currentValue: 138,
            explanation: 'Mock anomaly: +38% vs baseline.',
            acknowledged: false,
          },
        ],
      },
    })),
];

// ---------- migrations (honest empty) -----------------------------------------

const migrationHandlers = [
  http.get('/beacon/api/migrations/jobs', () => HttpResponse.json(paged([]))),
  http.get('/beacon/api/migrations/jobs/:id', () => new HttpResponse(null, { status: 404 })),
  http.get('/beacon/api/migrations/executions', () =>
    HttpResponse.json(paged([]))),
];

// ---------- admin / users / settings / data-quality --------------------------

const adminHandlers = [
  // API keys list (raw key only ever returned once on create — list omits it).
  http.get('/beacon/api/api-keys', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          name: 'Demo CI Key',
          prefix: 'sk-sem_demo',
          scopes: ['Read', 'Execute'],
          allowedProjectIds: null,
          createdAt: '2026-06-01T09:00:00Z',
          lastUsedAt: '2026-06-24T14:30:00Z',
          expiresAt: null,
          isActive: true,
        },
      ]))),
  // Every user's keys (administrators) — honest empty in the mock.
  http.get('/beacon/api/api-keys/admin', () => HttpResponse.json(paged([]))),

  // Users + roles (admin user management).
  http.get('/beacon/api/users', () =>
    HttpResponse.json(paged([
        {
          id: 1,
          userName: 'mock.admin',
          email: 'mock.admin@example.test',
          displayName: 'Mock Admin',
          isInternalUser: true,
          isSuperAdmin: true,
          isEnabled: true,
          lastLoginAt: '2026-06-25T08:00:00Z',
          roles: [{ id: 1, name: 'Admin', level: 100 }],
        },
      ]))),
  http.get('/beacon/api/users/roles', () =>
    HttpResponse.json({
      entries: [
        { id: 1, name: 'Admin', description: 'Full access', level: 100, isSystemRole: true },
        { id: 2, name: 'Member', description: 'Standard access', level: 10, isSystemRole: true },
        { id: 3, name: 'Approver', description: 'Approves query changes', level: 0, isSystemRole: true, permission: 'Approver' },
        { id: 4, name: 'Auditor', description: 'Reads the audit and settings', level: 0, isSystemRole: true, permission: 'Auditor' },
      ],
    })),
  http.put('/beacon/api/users/:id/roles', () => new HttpResponse(null, { status: 204 })),
  http.post('/beacon/api/users/:id/sign-out-everywhere', () => new HttpResponse(null, { status: 204 })),
  // What a user owns, created or changed last — honest empty in the mock.
  http.get('/beacon/api/users/:id/owned-resources', () => HttpResponse.json(paged([]))),

  // Security audit trail (Auditor) — honest empty in the mock; a verification checks nothing (outcome empty, no event found).
  http.get('/beacon/api/security-audit', () => HttpResponse.json(paged([]))),
  http.post('/beacon/api/security-audit/verify', async ({ request }) => {
    const body = (await request.json()) as { eventIds?: number[] | null };
    return HttpResponse.json({
      checkedCount: 0,
      intactCount: 0,
      modifiedCount: 0,
      unknownKeyCount: 0,
      outOfRangeCount: 0,
      outcome: 'empty',
      failedEventIds: [],
      verdicts: body.eventIds ? body.eventIds.map(eventId => ({ eventId, verdict: 'not_found' })) : null,
    });
  }),

  // Admin (LLM provider) settings — no secrets are ever returned, only "*Set" booleans.
  http.get('/beacon/api/admin-settings', () =>
    HttpResponse.json({
      settings: {
        baseUrl: 'https://demo.beacon.test',
        llmProvider: null,
        llmApiKeySet: false,
        llmEndpointSet: false,
        llmRegion: null,
        llmSessionTokenSet: false,
        llmAwsAccessKeyIdSet: false,
        llmAwsSecretAccessKeySet: false,
        llmBedrockAuthMode: BedrockAuthMode.IamRole,
        llmModel: null,
        llmFastModel: null,
        llmMaxConcurrentRequests: 4,
        llmTokensPerMinute: 100000,
        llmRequestsPerMinute: 60,
        llmMonthlyBudget: 0,
        llmRateLimitsEnabled: false,
      },
      history: [],
    })),

  // The LLM providers the host installed: all four, as in the sample host (Bedrock needs no API key).
  http.get('/beacon/api/admin-settings/llm-providers', () =>
    HttpResponse.json({
      aiEnabled: true,
      providers: [
        { provider: AiProvider.OpenAI, requiresApiKey: true },
        { provider: AiProvider.Claude, requiresApiKey: true },
        { provider: AiProvider.AzureOpenAI, requiresApiKey: true },
        { provider: AiProvider.Bedrock, requiresApiKey: false },
      ],
    })),

  // Current-user settings (profile self-service).
  http.get('/beacon/api/user-settings', () =>
    HttpResponse.json({
      user: {
        userName: 'mock.admin',
        email: 'mock.admin@example.test',
        displayName: 'Mock Admin',
        isInternalUser: true,
        roles: ['Admin'],
      },
    })),

  // Data quality (overview + contracts are top-level arrays).
  http.get('/beacon/api/data-quality/overview', () => HttpResponse.json([])),
  http.get('/beacon/api/data-quality/contracts', () => HttpResponse.json(paged([]))),
];

// ---------- catch-alls (keep LAST) --------------------------------------------

const fallbackHandlers = [
  // SignalR negotiate — fail fast so the hub gives up with one warning.
  http.post('/beacon/api/hub/negotiate', () =>
    problem(503, 'Mock mode', 'SignalR hub is not available in mock mode.')),
  http.get('/beacon/api/*', ({ request }) =>
    problem(404, 'Mock mode', `No mock handler for GET ${new URL(request.url).pathname}.`)),
  http.post('/beacon/api/*', () =>
    problem(400, 'Mock mode', 'Mock mode: mutation not mocked.')),
  http.put('/beacon/api/*', () =>
    problem(400, 'Mock mode', 'Mock mode: mutation not mocked.')),
  http.delete('/beacon/api/*', () =>
    problem(400, 'Mock mode', 'Mock mode: mutation not mocked.')),
  http.patch('/beacon/api/*', () =>
    problem(400, 'Mock mode', 'Mock mode: mutation not mocked.')),
];

export const handlers = [
  ...authHandlers,
  ...homeHandlers,
  ...projectsHandlers,
  ...queriesHandlers,
  ...dataSourcesHandlers,
  ...listHandlers,
  ...aiActorsHandlers,
  ...mcpHandlers,
  ...controlTowerHandlers,
  ...migrationHandlers,
  ...adminHandlers,
  ...fallbackHandlers,
];
