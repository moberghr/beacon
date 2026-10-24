import { Suspense, type ComponentType } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Toaster } from 'sonner';
import { RequireAuth } from './auth/RequireAuth';
import { GATEWAY_LANDING_PATH, RequireFullBeacon } from './auth/RequireFullBeacon';
import { RequireMigrations } from './auth/RequireMigrations';
import { useAccess } from './auth/useAuth';
import { AppShell } from './components/layout/AppShell';
import { RouteErrorBoundary } from './components/RouteErrorBoundary';
import { lazyWithRetry } from './lib/lazyWithRetry';
import { routeChunks } from './routeChunks';

const ProjectsListPage = lazyWithRetry(routeChunks.ProjectsListPage);
const ProjectDetailPage = lazyWithRetry(routeChunks.ProjectDetailPage);
const NotificationsPage = lazyWithRetry(routeChunks.NotificationsPage);
const HomePage = lazyWithRetry(routeChunks.HomePage);
const AboutPage = lazyWithRetry(routeChunks.AboutPage);
const ControlTowerPage = lazyWithRetry(routeChunks.ControlTowerPage);
const MigrationHistoryPage = lazyWithRetry(routeChunks.MigrationHistoryPage);
const QueriesListPage = lazyWithRetry(routeChunks.QueriesListPage);
const QueryDetailPage = lazyWithRetry(routeChunks.QueryDetailPage);
const QueryEditorPage = lazyWithRetry(routeChunks.QueryEditorPage);
const NewQueryPage = lazyWithRetry(routeChunks.NewQueryPage);
const QueryVersionsPage = lazyWithRetry(routeChunks.QueryVersionsPage);
const QueryVersionDetailPage = lazyWithRetry(routeChunks.QueryVersionDetailPage);
const RecipientsListPage = lazyWithRetry(routeChunks.RecipientsListPage);
const QueryFoldersPage = lazyWithRetry(routeChunks.QueryFoldersPage);
const GlossaryPage = lazyWithRetry(routeChunks.GlossaryPage);
const TasksListPage = lazyWithRetry(routeChunks.TasksListPage);
const TaskDetailPage = lazyWithRetry(routeChunks.TaskDetailPage);
const ApprovalsListPage = lazyWithRetry(routeChunks.ApprovalsListPage);
const ApiKeysListPage = lazyWithRetry(routeChunks.ApiKeysListPage);
const UsersListPage = lazyWithRetry(routeChunks.UsersListPage);
const SubscriptionsListPage = lazyWithRetry(routeChunks.SubscriptionsListPage);
const SubscriptionDetailPage = lazyWithRetry(routeChunks.SubscriptionDetailPage);
const DataSourcesListPage = lazyWithRetry(routeChunks.DataSourcesListPage);
const DataSourceDetailPage = lazyWithRetry(routeChunks.DataSourceDetailPage);
const RelationshipsPage = lazyWithRetry(routeChunks.RelationshipsPage);
const NewProjectPage = lazyWithRetry(routeChunks.NewProjectPage);
const NewMigrationJobPage = lazyWithRetry(routeChunks.NewMigrationJobPage);
const MigrationJobDetailPage = lazyWithRetry(routeChunks.MigrationJobDetailPage);
const ApprovalDetailPage = lazyWithRetry(routeChunks.ApprovalDetailPage);
const ChangeApprovalDetailPage = lazyWithRetry(routeChunks.ChangeApprovalDetailPage);
const AiActorEditPage = lazyWithRetry(routeChunks.AiActorEditPage);
const AdminSettingsPage = lazyWithRetry(routeChunks.AdminSettingsPage);
const SettingsPage = lazyWithRetry(routeChunks.SettingsPage);
const NotificationDetailPage = lazyWithRetry(routeChunks.NotificationDetailPage);
const DataQualityPage = lazyWithRetry(routeChunks.DataQualityPage);
const DataContractDetailPage = lazyWithRetry(routeChunks.DataContractDetailPage);
const McpPlaygroundPage = lazyWithRetry(routeChunks.McpPlaygroundPage);
const McpLearningPage = lazyWithRetry(routeChunks.McpLearningPage);
const McpSettingsPage = lazyWithRetry(routeChunks.McpSettingsPage);
const McpAuditPage = lazyWithRetry(routeChunks.McpAuditPage);
const SecurityAuditPage = lazyWithRetry(routeChunks.SecurityAuditPage);
const AiActorsListPage = lazyWithRetry(routeChunks.AiActorsListPage);
const AiActorDetailPage = lazyWithRetry(routeChunks.AiActorDetailPage);
const MigrationJobsListPage = lazyWithRetry(routeChunks.MigrationJobsListPage);
const LoginPage = lazyWithRetry(routeChunks.LoginPage);
const LogoutPage = lazyWithRetry(routeChunks.LogoutPage);
const SetupPage = lazyWithRetry(routeChunks.SetupPage);
const ErrorPage = lazyWithRetry(routeChunks.ErrorPage);

function PageFallback() {
  return (
    <div className="grid place-items-center min-h-[60vh] p-7">
      <span className="text-text-muted text-sm">Loading page…</span>
    </div>
  );
}

function lazyRoute(Element: ComponentType) {
  return (
    <RouteErrorBoundary>
      <Suspense fallback={<PageFallback />}>
        <Element />
      </Suspense>
    </RouteErrorBoundary>
  );
}

/** `/home` is the landing page of the full Beacon; the MCP gateway has none and lands on its MCP pages. */
function HomeRoute() {
  const access = useAccess();
  if (access.isLoading) return null;
  if (access.gatewayMode) return <Navigate to={GATEWAY_LANDING_PATH} replace />;
  return lazyRoute(HomePage);
}

/** The catch-all leads home, or to the gateway landing page in gateway mode. */
function FallbackRoute() {
  const access = useAccess();
  if (access.isLoading) return null;
  return <Navigate to={access.gatewayMode ? GATEWAY_LANDING_PATH : '/home'} replace />;
}

export default function App() {
  return (
    <BrowserRouter basename="/beacon">
      <Routes>
        {/* Anonymous (auth landing) routes — must NOT be wrapped in RequireAuth */}
        <Route path="/login" element={lazyRoute(LoginPage)} />
        <Route path="/logout" element={lazyRoute(LogoutPage)} />
        <Route path="/setup" element={lazyRoute(SetupPage)} />
        <Route path="/error" element={lazyRoute(ErrorPage)} />

        {/* Authenticated routes — wrapped */}
        <Route
          path="*"
          element={
            <RequireAuth>
              <Routes>
                <Route element={<AppShell />}>
                  <Route path="/" element={<Navigate to="/home" replace />} />
                  <Route path="/home" element={<HomeRoute />} />
                  <Route path="/about" element={lazyRoute(AboutPage)} />
                  <Route path="/projects" element={lazyRoute(ProjectsListPage)} />
                  <Route element={<RequireFullBeacon />}>
                    <Route path="/projects/new" element={lazyRoute(NewProjectPage)} />
                  </Route>
                  <Route path="/projects/:id" element={lazyRoute(ProjectDetailPage)} />
                  <Route path="/mcp-learning" element={lazyRoute(McpLearningPage)} />
                  <Route path="/mcp-settings" element={lazyRoute(McpSettingsPage)} />
                  <Route path="/mcp-audit" element={lazyRoute(McpAuditPage)} />
                  {/* The MCP gateway serves only the projects and MCP pages. */}
                  <Route element={<RequireFullBeacon />}>
                    <Route path="/settings" element={lazyRoute(SettingsPage)} />
                    <Route path="/notifications" element={lazyRoute(NotificationsPage)} />
                    <Route path="/notifications/:id" element={lazyRoute(NotificationDetailPage)} />
                    <Route path="/control-tower" element={lazyRoute(ControlTowerPage)} />
                    {/* Data migration exists only while the deployment runs it (Beacon:Migrations:Enabled). */}
                    <Route element={<RequireMigrations />}>
                      <Route path="/migration-history" element={lazyRoute(MigrationHistoryPage)} />
                      <Route path="/migration-jobs" element={lazyRoute(MigrationJobsListPage)} />
                      <Route path="/migration-jobs/new" element={lazyRoute(NewMigrationJobPage)} />
                      <Route path="/migration-jobs/:id" element={lazyRoute(MigrationJobDetailPage)} />
                    </Route>
                    <Route path="/queries" element={lazyRoute(QueriesListPage)} />
                    <Route path="/queries/new" element={lazyRoute(NewQueryPage)} />
                    <Route path="/queries/:id" element={lazyRoute(QueryDetailPage)} />
                    <Route path="/queries/:id/edit" element={lazyRoute(QueryEditorPage)} />
                    <Route path="/queries/:id/versions" element={lazyRoute(QueryVersionsPage)} />
                    <Route path="/queries/:id/versions/:versionId" element={lazyRoute(QueryVersionDetailPage)} />
                    <Route path="/query-folders" element={lazyRoute(QueryFoldersPage)} />
                    <Route path="/glossary" element={lazyRoute(GlossaryPage)} />
                    <Route path="/recipients" element={lazyRoute(RecipientsListPage)} />
                    <Route path="/tasks" element={lazyRoute(TasksListPage)} />
                    <Route path="/tasks/:id" element={lazyRoute(TaskDetailPage)} />
                    <Route path="/approvals" element={lazyRoute(ApprovalsListPage)} />
                    <Route path="/approvals/:id" element={lazyRoute(ApprovalDetailPage)} />
                    <Route path="/approvals/changes/:id" element={lazyRoute(ChangeApprovalDetailPage)} />
                    <Route path="/api-keys" element={lazyRoute(ApiKeysListPage)} />
                    <Route path="/users" element={lazyRoute(UsersListPage)} />
                    <Route path="/subscriptions" element={lazyRoute(SubscriptionsListPage)} />
                    <Route path="/subscriptions/:id" element={lazyRoute(SubscriptionDetailPage)} />
                    <Route path="/data-sources" element={lazyRoute(DataSourcesListPage)} />
                    <Route path="/data-sources/:id" element={lazyRoute(DataSourceDetailPage)} />
                    <Route path="/data-sources/:id/relationships" element={lazyRoute(RelationshipsPage)} />
                    <Route path="/admin-settings" element={lazyRoute(AdminSettingsPage)} />
                    <Route path="/security-audit" element={lazyRoute(SecurityAuditPage)} />
                    <Route path="/data-quality" element={lazyRoute(DataQualityPage)} />
                    <Route path="/data-quality/:id" element={lazyRoute(DataContractDetailPage)} />
                    <Route path="/mcp-playground" element={lazyRoute(McpPlaygroundPage)} />
                    <Route path="/ai-actors" element={lazyRoute(AiActorsListPage)} />
                    <Route path="/ai-actors/:id" element={lazyRoute(AiActorDetailPage)} />
                    <Route path="/ai-actors/:id/edit" element={lazyRoute(AiActorEditPage)} />
                  </Route>
                  <Route path="*" element={<FallbackRoute />} />
                </Route>
              </Routes>
            </RequireAuth>
          }
        />
      </Routes>
      <Toaster
        position="top-right"
        offset={16}
        gap={8}
        toastOptions={{
          duration: 4000,
          classNames: {
            toast: 'beacon-toast',
            title: 'beacon-toast-title',
            description: 'beacon-toast-description',
            actionButton: 'beacon-toast-action',
            cancelButton: 'beacon-toast-cancel',
            closeButton: 'beacon-toast-close',
          },
        }}
      />
    </BrowserRouter>
  );
}
