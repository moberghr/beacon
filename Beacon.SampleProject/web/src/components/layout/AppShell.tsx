import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar';

/**
 * Top-level layout. Two-column grid: sticky sidebar + scrollable main.
 * Matches `.app` class in styles-beacon.css.
 */
export function AppShell() {
  return (
    <div className="app">
      <Sidebar />
      <main>
        <Outlet />
      </main>
    </div>
  );
}
