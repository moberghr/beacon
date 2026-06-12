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
 */
import { http, HttpResponse } from 'msw';
import {
  ApprovalStatus,
  ChangeSource,
  HealthStatus,
  NotificationStatus,
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
      displayName: 'Mock Admin',
      email: 'mock.admin@example.test',
      isAuthenticated: true,
      roles: ['Admin'],
    })),
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
    HttpResponse.json({
      entries: [
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
        },
      ],
    })),
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
          { name: 'Demo Warehouse (PostgreSQL)', type: 'Database', tableCount: 96, qualityScore: 91 },
          { name: 'Sample CRM (SQL Server)', type: 'Database', tableCount: 32, qualityScore: 78 },
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
      },
    })),
  http.get('/beacon/api/projects/:id/documentation', () =>
    HttpResponse.json({ latest: null, history: [] })),
];

// ---------- queries ----------------------------------------------------------

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
    })),
  http.get('/beacon/api/query-folders', () => HttpResponse.json({ folders: [] })),
];

// ---------- data sources -----------------------------------------------------

const dataSourcesHandlers = [
  http.get('/beacon/api/data-sources', () =>
    HttpResponse.json({
      entries: [
        {
          id: 1,
          name: 'Demo Warehouse (PostgreSQL)',
          dataSourceType: 'Database',
          databaseEngineType: 'PostgreSQL',
          queryCount: 2,
          migrationJobsCount: 0,
          metadataLoadingEnabled: true,
        },
        {
          id: 2,
          name: 'Sample CRM (SQL Server)',
          dataSourceType: 'Database',
          databaseEngineType: 'MSSQL',
          queryCount: 1,
          migrationJobsCount: 0,
          metadataLoadingEnabled: false,
        },
      ],
    })),
];

// ---------- notifications / tasks / approvals / subscriptions ----------------

const listHandlers = [
  http.get('/beacon/api/notifications', () =>
    HttpResponse.json({
      entries: [
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
      ],
      totalCount: 2,
    })),
  http.get('/beacon/api/tasks', () =>
    HttpResponse.json({
      entries: [
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
    })),
  http.get('/beacon/api/approvals/pending', () =>
    HttpResponse.json([
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
    ])),
  http.get('/beacon/api/subscriptions', () =>
    HttpResponse.json({
      entries: [
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
      ],
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
      },
    })),
  http.get('/beacon/api/control-tower/health', () =>
    HttpResponse.json({
      entries: [
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
          lastExecutionStatus: NotificationStatus.NoResults,
          lastResultCount: 0,
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
    })),
];

// ---------- migrations (honest empty) -----------------------------------------

const migrationHandlers = [
  http.get('/beacon/api/migrations/jobs', () => HttpResponse.json({ jobs: [] })),
  http.get('/beacon/api/migrations/executions', () =>
    HttpResponse.json({ executions: [], totalCount: 0 })),
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
  ...controlTowerHandlers,
  ...migrationHandlers,
  ...fallbackHandlers,
];
