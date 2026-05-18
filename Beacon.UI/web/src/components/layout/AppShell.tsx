import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';
import { useHubInvalidations } from '@/lib/useHubInvalidations';

/**
 * Top-level layout. Sticky sidebar + scrollable main column.
 */
export function AppShell() {
  useHubInvalidations();
  return (
    <div className="grid grid-cols-[260px_1fr] min-h-screen bg-bg text-text">
      <Sidebar />
      <main className="min-w-0 overflow-x-hidden">
        <Outlet />
      </main>
    </div>
  );
}
