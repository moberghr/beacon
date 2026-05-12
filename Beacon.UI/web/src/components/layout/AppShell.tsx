import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';

/**
 * Top-level layout. Sticky sidebar + scrollable main column.
 */
export function AppShell() {
  return (
    <div className="grid grid-cols-[260px_1fr] min-h-screen bg-bg text-text">
      <Sidebar />
      <main className="min-w-0 overflow-x-hidden">
        <Outlet />
      </main>
    </div>
  );
}
