import { Suspense, type ComponentType } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Toaster } from 'sonner';
import { RequireAuth } from './auth/RequireAuth';
import { AppShell } from './components/layout/AppShell';
import { RouteErrorBoundary } from './components/RouteErrorBoundary';
import { lazyWithRetry } from './lib/lazyWithRetry';

const ProjectsListPage = lazyWithRetry(() => import('./routes/projects/ProjectsListPage'));
const ProjectDetailPage = lazyWithRetry(() => import('./routes/projects/ProjectDetailPage'));
const NotificationsPage = lazyWithRetry(() => import('./routes/notifications/NotificationsPage'));
const HomePage = lazyWithRetry(() => import('./routes/home/HomePage'));
const AboutPage = lazyWithRetry(() => import('./routes/about/AboutPage'));
const ControlTowerPage = lazyWithRetry(() => import('./routes/control-tower/ControlTowerPage'));
const MigrationHistoryPage = lazyWithRetry(() => import('./routes/migration-history/MigrationHistoryPage'));
const QueriesListPage = lazyWithRetry(() => import('./routes/queries/QueriesListPage'));
const QueryDetailPage = lazyWithRetry(() => import('./routes/queries/QueryDetailPage'));
const QueryEditorPage = lazyWithRetry(() => import('./routes/queries/QueryEditorPage'));
const NewQueryPage = lazyWithRetry(() => import('./routes/queries/NewQueryPage'));
const QueryVersionsPage = lazyWithRetry(() => import('./routes/queries/QueryVersionsPage'));
const QueryVersionDetailPage = lazyWithRetry(() => import('./routes/queries/QueryVersionDetailPage'));
const RecipientsListPage = lazyWithRetry(() => import('./routes/recipients/RecipientsListPage'));
const TasksListPage = lazyWithRetry(() => import('./routes/tasks/TasksListPage'));
const TaskDetailPage = lazyWithRetry(() => import('./routes/tasks/TaskDetailPage'));
const ApprovalsListPage = lazyWithRetry(() => import('./routes/approvals/ApprovalsListPage'));
const ApiKeysListPage = lazyWithRetry(() => import('./routes/api-keys/ApiKeysListPage'));
const UsersListPage = lazyWithRetry(() => import('./routes/users/UsersListPage'));
const SubscriptionsListPage = lazyWithRetry(() => import('./routes/subscriptions/SubscriptionsListPage'));
const SubscriptionDetailPage = lazyWithRetry(() => import('./routes/subscriptions/SubscriptionDetailPage'));
const DataSourcesListPage = lazyWithRetry(() => import('./routes/data-sources/DataSourcesListPage'));
const DataSourceDetailPage = lazyWithRetry(() => import('./routes/data-sources/DataSourceDetailPage'));
const NewProjectPage = lazyWithRetry(() => import('./routes/projects/NewProjectPage'));
const NewMigrationJobPage = lazyWithRetry(() => import('./routes/migration-jobs/NewMigrationJobPage'));
const MigrationJobDetailPage = lazyWithRetry(() => import('./routes/migration-jobs/MigrationJobDetailPage'));
const ApprovalDetailPage = lazyWithRetry(() => import('./routes/approvals/ApprovalDetailPage'));
const AiActorEditPage = lazyWithRetry(() => import('./routes/ai-actors/AiActorEditPage'));
const AdminSettingsPage = lazyWithRetry(() => import('./routes/admin-settings/AdminSettingsPage'));
const SettingsPage = lazyWithRetry(() => import('./routes/settings/SettingsPage'));
const NotificationDetailPage = lazyWithRetry(() => import('./routes/notifications/NotificationDetailPage'));
const DataQualityPage = lazyWithRetry(() => import('./routes/data-quality/DataQualityPage'));
const DataContractDetailPage = lazyWithRetry(() => import('./routes/data-quality/DataContractDetailPage'));
const McpPlaygroundPage = lazyWithRetry(() => import('./routes/mcp/McpPlaygroundPage'));
const McpLearningPage = lazyWithRetry(() => import('./routes/mcp/McpLearningPage'));
const McpSettingsPage = lazyWithRetry(() => import('./routes/mcp/McpSettingsPage'));
const AiActorsListPage = lazyWithRetry(() => import('./routes/ai-actors/AiActorsListPage'));
const AiActorDetailPage = lazyWithRetry(() => import('./routes/ai-actors/AiActorDetailPage'));
const MigrationJobsListPage = lazyWithRetry(() => import('./routes/migration-jobs/MigrationJobsListPage'));
const LoginPage = lazyWithRetry(() => import('./routes/auth/LoginPage'));
const LogoutPage = lazyWithRetry(() => import('./routes/auth/LogoutPage'));
const SetupPage = lazyWithRetry(() => import('./routes/auth/SetupPage'));
const ErrorPage = lazyWithRetry(() => import('./routes/auth/ErrorPage'));

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

export default function App() {
  return (
    <BrowserRouter basename="/">
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
                  <Route path="/home" element={lazyRoute(HomePage)} />
                  <Route path="/about" element={lazyRoute(AboutPage)} />
                  <Route path="/projects" element={lazyRoute(ProjectsListPage)} />
                  <Route path="/projects/new" element={lazyRoute(NewProjectPage)} />
                  <Route path="/projects/:id" element={lazyRoute(ProjectDetailPage)} />
                  <Route path="/notifications" element={lazyRoute(NotificationsPage)} />
                  <Route path="/notifications/:id" element={lazyRoute(NotificationDetailPage)} />
                  <Route path="/control-tower" element={lazyRoute(ControlTowerPage)} />
                  <Route path="/migration-history" element={lazyRoute(MigrationHistoryPage)} />
                  <Route path="/migration-jobs" element={lazyRoute(MigrationJobsListPage)} />
                  <Route path="/migration-jobs/new" element={lazyRoute(NewMigrationJobPage)} />
                  <Route path="/migration-jobs/:id" element={lazyRoute(MigrationJobDetailPage)} />
                  <Route path="/queries" element={lazyRoute(QueriesListPage)} />
                  <Route path="/queries/new" element={lazyRoute(NewQueryPage)} />
                  <Route path="/queries/:id" element={lazyRoute(QueryDetailPage)} />
                  <Route path="/queries/:id/edit" element={lazyRoute(QueryEditorPage)} />
                  <Route path="/queries/:id/versions" element={lazyRoute(QueryVersionsPage)} />
                  <Route path="/queries/:id/versions/:versionId" element={lazyRoute(QueryVersionDetailPage)} />
                  <Route path="/recipients" element={lazyRoute(RecipientsListPage)} />
                  <Route path="/tasks" element={lazyRoute(TasksListPage)} />
                  <Route path="/tasks/:id" element={lazyRoute(TaskDetailPage)} />
                  <Route path="/approvals" element={lazyRoute(ApprovalsListPage)} />
                  <Route path="/approvals/:id" element={lazyRoute(ApprovalDetailPage)} />
                  <Route path="/api-keys" element={lazyRoute(ApiKeysListPage)} />
                  <Route path="/users" element={lazyRoute(UsersListPage)} />
                  <Route path="/subscriptions" element={lazyRoute(SubscriptionsListPage)} />
                  <Route path="/subscriptions/:id" element={lazyRoute(SubscriptionDetailPage)} />
                  <Route path="/data-sources" element={lazyRoute(DataSourcesListPage)} />
                  <Route path="/data-sources/:id" element={lazyRoute(DataSourceDetailPage)} />
                  <Route path="/admin-settings" element={lazyRoute(AdminSettingsPage)} />
                  <Route path="/settings" element={lazyRoute(SettingsPage)} />
                  <Route path="/data-quality" element={lazyRoute(DataQualityPage)} />
                  <Route path="/data-quality/:id" element={lazyRoute(DataContractDetailPage)} />
                  <Route path="/mcp-playground" element={lazyRoute(McpPlaygroundPage)} />
                  <Route path="/mcp-learning" element={lazyRoute(McpLearningPage)} />
                  <Route path="/mcp-settings" element={lazyRoute(McpSettingsPage)} />
                  {/*
                    Dashboards are intentionally hidden pending a redesign (see UNFINISHED.md).
                    The route components still live under ./routes/dashboards/* for that future
                    work, but every dashboards path redirects to /home so direct-URL navigation
                    doesn't expose the unfinished feature.
                  */}
                  <Route path="/dashboards" element={<Navigate to="/home" replace />} />
                  <Route path="/dashboards/:id" element={<Navigate to="/home" replace />} />
                  <Route path="/dashboards/:id/edit" element={<Navigate to="/home" replace />} />
                  <Route path="/ai-actors" element={lazyRoute(AiActorsListPage)} />
                  <Route path="/ai-actors/:id" element={lazyRoute(AiActorDetailPage)} />
                  <Route path="/ai-actors/:id/edit" element={lazyRoute(AiActorEditPage)} />
                  <Route path="*" element={<Navigate to="/home" replace />} />
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
