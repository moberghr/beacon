// Route chunk loaders, shared by the lazy route components and the early prefetch in main.tsx.
// A dynamic import() of the same module is fetched once, so prefetching never loads a chunk twice.

import { matchPath } from 'react-router-dom';

export const routeChunks = {
  ProjectsListPage: () => import('./routes/projects/ProjectsListPage'),
  ProjectDetailPage: () => import('./routes/projects/ProjectDetailPage'),
  NotificationsPage: () => import('./routes/notifications/NotificationsPage'),
  HomePage: () => import('./routes/home/HomePage'),
  AboutPage: () => import('./routes/about/AboutPage'),
  ControlTowerPage: () => import('./routes/control-tower/ControlTowerPage'),
  MigrationHistoryPage: () => import('./routes/migration-history/MigrationHistoryPage'),
  QueriesListPage: () => import('./routes/queries/QueriesListPage'),
  QueryDetailPage: () => import('./routes/queries/QueryDetailPage'),
  QueryEditorPage: () => import('./routes/queries/QueryEditorPage'),
  NewQueryPage: () => import('./routes/queries/NewQueryPage'),
  QueryVersionsPage: () => import('./routes/queries/QueryVersionsPage'),
  QueryVersionDetailPage: () => import('./routes/queries/QueryVersionDetailPage'),
  RecipientsListPage: () => import('./routes/recipients/RecipientsListPage'),
  TasksListPage: () => import('./routes/tasks/TasksListPage'),
  TaskDetailPage: () => import('./routes/tasks/TaskDetailPage'),
  ApprovalsListPage: () => import('./routes/approvals/ApprovalsListPage'),
  ApiKeysListPage: () => import('./routes/api-keys/ApiKeysListPage'),
  UsersListPage: () => import('./routes/users/UsersListPage'),
  SubscriptionsListPage: () => import('./routes/subscriptions/SubscriptionsListPage'),
  SubscriptionDetailPage: () => import('./routes/subscriptions/SubscriptionDetailPage'),
  DataSourcesListPage: () => import('./routes/data-sources/DataSourcesListPage'),
  DataSourceDetailPage: () => import('./routes/data-sources/DataSourceDetailPage'),
  RelationshipsPage: () => import('./routes/data-sources/relationships/RelationshipsPage'),
  NewProjectPage: () => import('./routes/projects/NewProjectPage'),
  NewMigrationJobPage: () => import('./routes/migration-jobs/NewMigrationJobPage'),
  MigrationJobDetailPage: () => import('./routes/migration-jobs/MigrationJobDetailPage'),
  ApprovalDetailPage: () => import('./routes/approvals/ApprovalDetailPage'),
  ChangeApprovalDetailPage: () => import('./routes/approvals/ChangeApprovalDetailPage'),
  AiActorEditPage: () => import('./routes/ai-actors/AiActorEditPage'),
  AdminSettingsPage: () => import('./routes/admin-settings/AdminSettingsPage'),
  SettingsPage: () => import('./routes/settings/SettingsPage'),
  NotificationDetailPage: () => import('./routes/notifications/NotificationDetailPage'),
  DataQualityPage: () => import('./routes/data-quality/DataQualityPage'),
  DataContractDetailPage: () => import('./routes/data-quality/DataContractDetailPage'),
  McpPlaygroundPage: () => import('./routes/mcp/McpPlaygroundPage'),
  McpLearningPage: () => import('./routes/mcp/McpLearningPage'),
  McpSettingsPage: () => import('./routes/mcp/McpSettingsPage'),
  McpAuditPage: () => import('./routes/mcp/McpAuditPage'),
  SecurityAuditPage: () => import('./routes/security-audit/SecurityAuditPage'),
  AiActorsListPage: () => import('./routes/ai-actors/AiActorsListPage'),
  AiActorDetailPage: () => import('./routes/ai-actors/AiActorDetailPage'),
  MigrationJobsListPage: () => import('./routes/migration-jobs/MigrationJobsListPage'),
  LoginPage: () => import('./routes/auth/LoginPage'),
  LogoutPage: () => import('./routes/auth/LogoutPage'),
  SetupPage: () => import('./routes/auth/SetupPage'),
  ErrorPage: () => import('./routes/auth/ErrorPage'),
} as const;

/** Route patterns (relative to the router basename) and the chunk each one renders. */
const ROUTE_CHUNK_PATHS: ReadonlyArray<readonly [string, keyof typeof routeChunks]> = [
  ['/login', 'LoginPage'],
  ['/logout', 'LogoutPage'],
  ['/setup', 'SetupPage'],
  ['/error', 'ErrorPage'],
  ['/about', 'AboutPage'],
  ['/projects', 'ProjectsListPage'],
  ['/projects/new', 'NewProjectPage'],
  ['/projects/:id', 'ProjectDetailPage'],
  ['/mcp-learning', 'McpLearningPage'],
  ['/mcp-settings', 'McpSettingsPage'],
  ['/mcp-audit', 'McpAuditPage'],
  ['/settings', 'SettingsPage'],
  ['/notifications', 'NotificationsPage'],
  ['/notifications/:id', 'NotificationDetailPage'],
  ['/control-tower', 'ControlTowerPage'],
  ['/migration-history', 'MigrationHistoryPage'],
  ['/migration-jobs', 'MigrationJobsListPage'],
  ['/migration-jobs/new', 'NewMigrationJobPage'],
  ['/migration-jobs/:id', 'MigrationJobDetailPage'],
  ['/queries', 'QueriesListPage'],
  ['/queries/new', 'NewQueryPage'],
  ['/queries/:id', 'QueryDetailPage'],
  ['/queries/:id/edit', 'QueryEditorPage'],
  ['/queries/:id/versions', 'QueryVersionsPage'],
  ['/queries/:id/versions/:versionId', 'QueryVersionDetailPage'],
  ['/recipients', 'RecipientsListPage'],
  ['/tasks', 'TasksListPage'],
  ['/tasks/:id', 'TaskDetailPage'],
  ['/approvals', 'ApprovalsListPage'],
  ['/approvals/:id', 'ApprovalDetailPage'],
  ['/approvals/changes/:id', 'ChangeApprovalDetailPage'],
  ['/api-keys', 'ApiKeysListPage'],
  ['/users', 'UsersListPage'],
  ['/subscriptions', 'SubscriptionsListPage'],
  ['/subscriptions/:id', 'SubscriptionDetailPage'],
  ['/data-sources', 'DataSourcesListPage'],
  ['/data-sources/:id', 'DataSourceDetailPage'],
  ['/data-sources/:id/relationships', 'RelationshipsPage'],
  ['/admin-settings', 'AdminSettingsPage'],
  ['/security-audit', 'SecurityAuditPage'],
  ['/data-quality', 'DataQualityPage'],
  ['/data-quality/:id', 'DataContractDetailPage'],
  ['/mcp-playground', 'McpPlaygroundPage'],
  ['/ai-actors', 'AiActorsListPage'],
  ['/ai-actors/:id', 'AiActorDetailPage'],
  ['/ai-actors/:id/edit', 'AiActorEditPage'],
];

/**
 * Starts loading the page chunk for the URL the app was opened on, in parallel with the `/auth/me` request that
 * gates rendering it. Without this the chunk is only requested once the auth round trip has resolved.
 * `pathname` is the full location path including the router basename.
 */
export function prefetchRouteChunk(pathname: string, basename = '/beacon'): void {
  const path = pathname.startsWith(basename) ? pathname.slice(basename.length) || '/' : pathname;
  for (const [pattern, name] of ROUTE_CHUNK_PATHS) {
    if (matchPath({ path: pattern, end: true }, path)) {
      // A failed prefetch is not an error: the route's own lazy import retries and reports it.
      routeChunks[name]().catch(() => undefined);
      return;
    }
  }
}
