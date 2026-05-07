// Vendored from Beacon-design/sidebar.jsx, ported to TS + React Router.
// Each nav item carries a slug (React route under /app) and a blazorPath
// (Blazor route under /beacon). resolveNavHref picks based on feature flag.
import { Link, useLocation } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { resolveNavHref, isMigrated } from '@/feature-flags';
import { useAuth } from '@/auth/useAuth';

interface NavItem {
  name: string;
  icon: keyof typeof Icon;
  slug: string;
  blazorPath: string;
  count?: number;
  badge?: string;
}

interface NavSection {
  label: string;
  items: NavItem[];
}

const SECTIONS: NavSection[] = [
  {
    label: 'Overview',
    items: [
      { name: 'Home', icon: 'Home', slug: 'home', blazorPath: '' },
      { name: 'Control Tower', icon: 'Tower', slug: 'control-tower', blazorPath: 'control-tower' },
      { name: 'Dashboards', icon: 'Grid', slug: 'dashboards', blazorPath: 'dashboards' },
    ],
  },
  {
    label: 'Data',
    items: [
      { name: 'Data Sources', icon: 'Database', slug: 'data-sources', blazorPath: 'datasources' },
      { name: 'Projects', icon: 'Folder', slug: 'projects', blazorPath: 'projects' },
      { name: 'Data Quality', icon: 'Shield', slug: 'data-quality', blazorPath: 'dataquality' },
      { name: 'Data Migration', icon: 'ArrowsLR', slug: 'data-migration', blazorPath: 'migrationjobs' },
      { name: 'AI Actors', icon: 'Bot', slug: 'ai-actors', blazorPath: 'ai-actors' },
    ],
  },
  {
    label: 'Alerts',
    items: [
      { name: 'Queries', icon: 'Query', slug: 'queries', blazorPath: 'queries' },
      { name: 'Subscriptions', icon: 'Inbox', slug: 'subscriptions', blazorPath: 'subscriptions' },
      { name: 'Notifications', icon: 'Bell', slug: 'notifications', blazorPath: 'notifications' },
      { name: 'Recipients', icon: 'Users', slug: 'recipients', blazorPath: 'recipients' },
      { name: 'Tasks', icon: 'Check', slug: 'tasks', blazorPath: 'tasks' },
    ],
  },
  {
    label: 'MCP',
    items: [
      { name: 'Data Catalog', icon: 'Book', slug: 'data-catalog', blazorPath: 'datacatalog' },
      { name: 'API Keys', icon: 'Key', slug: 'api-keys', blazorPath: 'apikeys' },
      { name: 'MCP Settings', icon: 'Sliders', slug: 'mcp-settings', blazorPath: 'mcp-settings' },
      { name: 'MCP Playground', icon: 'Wand', slug: 'mcp-playground', blazorPath: 'mcp-playground' },
      { name: 'MCP Learning', icon: 'Lightbulb', slug: 'mcp-learning', blazorPath: 'mcp-learning' },
    ],
  },
  {
    label: 'Admin',
    items: [
      { name: 'User Management', icon: 'Users', slug: 'users', blazorPath: 'users' },
      { name: 'Admin Settings', icon: 'Cog', slug: 'admin-settings', blazorPath: 'adminsettings' },
    ],
  },
];

function userInitials(name: string | null | undefined): string {
  if (!name) return '?';
  const parts = name.split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

export function Sidebar() {
  const location = useLocation();
  const { data: user } = useAuth();

  return (
    <aside className="sidebar">
      <div className="sidebar__brand">
        <div className="sidebar__logo">
          <div className="sidebar__logo-dot" aria-hidden="true" />
          <span>Beacon</span>
        </div>
        <span className="sidebar__version">v0.93</span>
      </div>

      <nav className="sidebar__nav">
        {SECTIONS.map(section => (
          <div className="sidebar__section" key={section.label}>
            <div className="sidebar__section-label">{section.label}</div>
            {section.items.map(item => {
              const IconCmp = Icon[item.icon];
              const href = resolveNavHref(item.slug, item.blazorPath);
              const migrated = isMigrated(item.slug);
              const isActive = migrated && location.pathname.startsWith(`/${item.slug}`);

              const className = 'nav-item' + (isActive ? ' active' : '');
              const content = (
                <>
                  {IconCmp && <IconCmp className="nav-item__icon" />}
                  <span>{item.name}</span>
                  {item.badge && <span className="nav-item__badge">{item.badge}</span>}
                  {item.count != null && !item.badge && <span className="nav-item__count">{item.count}</span>}
                </>
              );

              return migrated ? (
                <Link key={item.name} to={href} className={className}>
                  {content}
                </Link>
              ) : (
                <a key={item.name} href={href} className={className}>
                  {content}
                </a>
              );
            })}
          </div>
        ))}
        <div className="sidebar__section" style={{ marginTop: 18 }}>
          <a href="/beacon/about" className="nav-item">
            <Icon.Info className="nav-item__icon" />
            <span>About</span>
          </a>
        </div>
      </nav>

      <div className="sidebar__footer">
        <div className="user-chip">
          <div className="user-chip__avatar">{userInitials(user?.displayName ?? user?.email)}</div>
          <div className="user-chip__info">
            <div className="user-chip__name">{user?.displayName ?? user?.email ?? '—'}</div>
            <div className="user-chip__role">
              {user?.roles && user.roles.length > 0 ? user.roles.join(' · ') : 'Authenticated'}
            </div>
          </div>
          <Icon.ChevronDown size={14} className="muted" />
        </div>
      </div>
    </aside>
  );
}
