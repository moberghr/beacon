import { lazy, Suspense, type ComponentType } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Toaster } from 'sonner';
import { RequireAuth } from './auth/RequireAuth';
import { AppShell } from './components/layout/AppShell';

const ProjectsListPage = lazy(() => import('./routes/projects/ProjectsListPage'));
const ProjectDetailPage = lazy(() => import('./routes/projects/ProjectDetailPage'));
const NotificationsPage = lazy(() => import('./routes/notifications/NotificationsPage'));
const HomePage = lazy(() => import('./routes/home/HomePage'));
const AboutPage = lazy(() => import('./routes/about/AboutPage'));
const ControlTowerPage = lazy(() => import('./routes/control-tower/ControlTowerPage'));
const MigrationHistoryPage = lazy(() => import('./routes/migration-history/MigrationHistoryPage'));
const QueryDetailPage = lazy(() => import('./routes/queries/QueryDetailPage'));
const QueryEditorPage = lazy(() => import('./routes/queries/QueryEditorPage'));
const QueryVersionsPage = lazy(() => import('./routes/queries/QueryVersionsPage'));
const QueryVersionDetailPage = lazy(() => import('./routes/queries/QueryVersionDetailPage'));
const RecipientsListPage = lazy(() => import('./routes/recipients/RecipientsListPage'));
const TasksListPage = lazy(() => import('./routes/tasks/TasksListPage'));
const TaskDetailPage = lazy(() => import('./routes/tasks/TaskDetailPage'));
const ApprovalsListPage = lazy(() => import('./routes/approvals/ApprovalsListPage'));
const ApiKeysListPage = lazy(() => import('./routes/api-keys/ApiKeysListPage'));
const UsersListPage = lazy(() => import('./routes/users/UsersListPage'));
const SubscriptionsListPage = lazy(() => import('./routes/subscriptions/SubscriptionsListPage'));
const SubscriptionDetailPage = lazy(() => import('./routes/subscriptions/SubscriptionDetailPage'));
const DataSourcesListPage = lazy(() => import('./routes/data-sources/DataSourcesListPage'));
const AdminSettingsPage = lazy(() => import('./routes/admin-settings/AdminSettingsPage'));
const SettingsPage = lazy(() => import('./routes/settings/SettingsPage'));
const NotificationDetailPage = lazy(() => import('./routes/notifications/NotificationDetailPage'));
const DataCatalogPage = lazy(() => import('./routes/data-catalog/DataCatalogPage'));
const DataQualityPage = lazy(() => import('./routes/data-quality/DataQualityPage'));
const DataContractDetailPage = lazy(() => import('./routes/data-quality/DataContractDetailPage'));
const McpPlaygroundPage = lazy(() => import('./routes/mcp/McpPlaygroundPage'));
const McpLearningPage = lazy(() => import('./routes/mcp/McpLearningPage'));
const McpSettingsPage = lazy(() => import('./routes/mcp/McpSettingsPage'));

function PageFallback() {
  return (
    <div className="page" style={{ display: 'grid', placeItems: 'center', minHeight: '60vh' }}>
      <span className="muted">Loading page…</span>
    </div>
  );
}

function lazyRoute(Element: ComponentType) {
  return (
    <Suspense fallback={<PageFallback />}>
      <Element />
    </Suspense>
  );
}

export default function App() {
  return (
    <BrowserRouter basename="/app">
      <RequireAuth>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/" element={<Navigate to="/home" replace />} />
            <Route path="/home" element={lazyRoute(HomePage)} />
            <Route path="/about" element={lazyRoute(AboutPage)} />
            <Route path="/projects" element={lazyRoute(ProjectsListPage)} />
            <Route path="/projects/:id" element={lazyRoute(ProjectDetailPage)} />
            <Route path="/notifications" element={lazyRoute(NotificationsPage)} />
            <Route path="/notifications/:id" element={lazyRoute(NotificationDetailPage)} />
            <Route path="/control-tower" element={lazyRoute(ControlTowerPage)} />
            <Route path="/migration-history" element={lazyRoute(MigrationHistoryPage)} />
            <Route path="/queries/:id" element={lazyRoute(QueryDetailPage)} />
            <Route path="/queries/:id/edit" element={lazyRoute(QueryEditorPage)} />
            <Route path="/queries/:id/versions" element={lazyRoute(QueryVersionsPage)} />
            <Route path="/queries/:id/versions/:versionId" element={lazyRoute(QueryVersionDetailPage)} />
            <Route path="/recipients" element={lazyRoute(RecipientsListPage)} />
            <Route path="/tasks" element={lazyRoute(TasksListPage)} />
            <Route path="/tasks/:id" element={lazyRoute(TaskDetailPage)} />
            <Route path="/approvals" element={lazyRoute(ApprovalsListPage)} />
            <Route path="/api-keys" element={lazyRoute(ApiKeysListPage)} />
            <Route path="/users" element={lazyRoute(UsersListPage)} />
            <Route path="/subscriptions" element={lazyRoute(SubscriptionsListPage)} />
            <Route path="/subscriptions/:id" element={lazyRoute(SubscriptionDetailPage)} />
            <Route path="/data-sources" element={lazyRoute(DataSourcesListPage)} />
            <Route path="/admin-settings" element={lazyRoute(AdminSettingsPage)} />
            <Route path="/settings" element={lazyRoute(SettingsPage)} />
            <Route path="/data-catalog" element={lazyRoute(DataCatalogPage)} />
            <Route path="/data-quality" element={lazyRoute(DataQualityPage)} />
            <Route path="/data-quality/:id" element={lazyRoute(DataContractDetailPage)} />
            <Route path="/mcp-playground" element={lazyRoute(McpPlaygroundPage)} />
            <Route path="/mcp-learning" element={lazyRoute(McpLearningPage)} />
            <Route path="/mcp-settings" element={lazyRoute(McpSettingsPage)} />
            <Route path="*" element={<Navigate to="/home" replace />} />
          </Route>
        </Routes>
      </RequireAuth>
      <Toaster richColors position="top-right" />
    </BrowserRouter>
  );
}
