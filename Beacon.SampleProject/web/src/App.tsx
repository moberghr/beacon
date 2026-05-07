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
const QueryVersionsPage = lazy(() => import('./routes/queries/QueryVersionsPage'));
const QueryVersionDetailPage = lazy(() => import('./routes/queries/QueryVersionDetailPage'));
const RecipientsListPage = lazy(() => import('./routes/recipients/RecipientsListPage'));
const TasksListPage = lazy(() => import('./routes/tasks/TasksListPage'));
const TaskDetailPage = lazy(() => import('./routes/tasks/TaskDetailPage'));
const ApprovalsListPage = lazy(() => import('./routes/approvals/ApprovalsListPage'));
const ApiKeysListPage = lazy(() => import('./routes/api-keys/ApiKeysListPage'));
const UsersListPage = lazy(() => import('./routes/users/UsersListPage'));

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
            <Route path="/control-tower" element={lazyRoute(ControlTowerPage)} />
            <Route path="/migration-history" element={lazyRoute(MigrationHistoryPage)} />
            <Route path="/queries/:id/versions" element={lazyRoute(QueryVersionsPage)} />
            <Route path="/queries/:id/versions/:versionId" element={lazyRoute(QueryVersionDetailPage)} />
            <Route path="/recipients" element={lazyRoute(RecipientsListPage)} />
            <Route path="/tasks" element={lazyRoute(TasksListPage)} />
            <Route path="/tasks/:id" element={lazyRoute(TaskDetailPage)} />
            <Route path="/approvals" element={lazyRoute(ApprovalsListPage)} />
            <Route path="/api-keys" element={lazyRoute(ApiKeysListPage)} />
            <Route path="/users" element={lazyRoute(UsersListPage)} />
            <Route path="*" element={<Navigate to="/home" replace />} />
          </Route>
        </Routes>
      </RequireAuth>
      <Toaster richColors position="top-right" />
    </BrowserRouter>
  );
}
