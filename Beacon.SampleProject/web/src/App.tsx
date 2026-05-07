import { lazy, Suspense } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { Toaster } from 'sonner';
import { RequireAuth } from './auth/RequireAuth';
import { AppShell } from './components/layout/AppShell';

const ProjectsListPage = lazy(() => import('./routes/projects/ProjectsListPage'));
const ProjectDetailPage = lazy(() => import('./routes/projects/ProjectDetailPage'));

function PageFallback() {
  return (
    <div className="page" style={{ display: 'grid', placeItems: 'center', minHeight: '60vh' }}>
      <span className="muted">Loading page…</span>
    </div>
  );
}

export default function App() {
  return (
    <BrowserRouter basename="/app">
      <RequireAuth>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="/" element={<Navigate to="/projects" replace />} />
            <Route
              path="/projects"
              element={
                <Suspense fallback={<PageFallback />}>
                  <ProjectsListPage />
                </Suspense>
              }
            />
            <Route
              path="/projects/:id"
              element={
                <Suspense fallback={<PageFallback />}>
                  <ProjectDetailPage />
                </Suspense>
              }
            />
            <Route path="*" element={<Navigate to="/projects" replace />} />
          </Route>
        </Routes>
      </RequireAuth>
      <Toaster richColors position="top-right" />
    </BrowserRouter>
  );
}
