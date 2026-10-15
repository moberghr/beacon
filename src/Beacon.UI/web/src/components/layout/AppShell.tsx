import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { useHubInvalidations } from '@/lib/useHubInvalidations';
import { FunctionalOnlyGate } from '@/auth/FunctionalOnlyGate';

/**
 * Top-level layout. Sticky sidebar + scrollable main column. A user with functional roles only sees the pages those
 * open; any other page explains that project data needs a Viewer or Editor role.
 */
export function AppShell() {
  useHubInvalidations();
  return (
    <div className="grid grid-cols-[260px_1fr] min-h-screen bg-bg text-text">
      <Sidebar />
      <main className="min-w-0 overflow-x-hidden">
        <FunctionalOnlyGate>
          <Outlet />
        </FunctionalOnlyGate>
      </main>
    </div>
  );
}
