// Beacon sidebar — Tailwind + Beacon design system.
import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  Home as IconHome,
  Radio as IconTower,
  Database as IconDatabase,
  Folder as IconFolder,
  Shield as IconShield,
  ArrowLeftRight as IconArrowsLR,
  Bot as IconBot,
  Search as IconQuery,
  Inbox as IconInbox,
  Bell as IconBell,
  Users as IconUsers,
  Check as IconCheck,
  Key as IconKey,
  SlidersHorizontal as IconSliders,
  Wand2 as IconWand,
  Lightbulb as IconLightbulb,
  Settings as IconCog,
  Info as IconInfo,
  ChevronDown,
  LogOut as IconLogOut,
  type LucideIcon,
} from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { cn } from '@/lib/cn';
import { useAuth } from '@/auth/useAuth';
import { beaconApi } from '@/api/client';
import { BuildBadge } from '@/components/beacon';

interface NavItem {
  name: string;
  Icon: LucideIcon;
  slug: string;
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
      { name: 'Home', Icon: IconHome, slug: 'home' },
      { name: 'Control Tower', Icon: IconTower, slug: 'control-tower' },
      // Dashboards hidden — feature unfinished, will be redesigned (see UNFINISHED.md).
    ],
  },
  {
    label: 'Data',
    items: [
      { name: 'Data Sources', Icon: IconDatabase, slug: 'data-sources' },
      { name: 'Projects', Icon: IconFolder, slug: 'projects' },
      { name: 'Data Quality', Icon: IconShield, slug: 'data-quality' },
      { name: 'Data Migration', Icon: IconArrowsLR, slug: 'migration-jobs' },
      { name: 'AI Actors', Icon: IconBot, slug: 'ai-actors' },
    ],
  },
  {
    label: 'Alerts',
    items: [
      { name: 'Queries', Icon: IconQuery, slug: 'queries' },
      { name: 'Subscriptions', Icon: IconInbox, slug: 'subscriptions' },
      { name: 'Notifications', Icon: IconBell, slug: 'notifications' },
      { name: 'Recipients', Icon: IconUsers, slug: 'recipients' },
      { name: 'Tasks', Icon: IconCheck, slug: 'tasks' },
    ],
  },
  {
    label: 'MCP',
    items: [
      { name: 'API Keys', Icon: IconKey, slug: 'api-keys' },
      { name: 'MCP Settings', Icon: IconSliders, slug: 'mcp-settings' },
      { name: 'MCP Playground', Icon: IconWand, slug: 'mcp-playground' },
      { name: 'MCP Learning', Icon: IconLightbulb, slug: 'mcp-learning' },
    ],
  },
  {
    label: 'Admin',
    items: [
      { name: 'User Management', Icon: IconUsers, slug: 'users' },
      { name: 'Admin Settings', Icon: IconCog, slug: 'admin-settings' },
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

const navItemBase =
  'flex items-center gap-2.5 px-2.5 py-1.5 rounded-sm text-sm text-text-muted transition ' +
  'hover:bg-surface-2 hover:text-text';
const navItemActive =
  'bg-brand-50 text-brand-700 dark:bg-brand-100 dark:text-brand-300 font-medium';

export function Sidebar() {
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { data: user } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const [signingOut, setSigningOut] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const menuPopRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!menuOpen) return;
    const onDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setMenuOpen(false);
        triggerRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    // Move focus into the menu on open.
    const items = menuPopRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]');
    items?.[0]?.focus();
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [menuOpen]);

  const onMenuKeyDown = (e: ReactKeyboardEvent) => {
    const items = Array.from(
      menuPopRef.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? [],
    );
    if (items.length === 0) return;
    const currentIndex = items.indexOf(document.activeElement as HTMLElement);
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      items[(currentIndex + 1) % items.length].focus();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      items[(currentIndex - 1 + items.length) % items.length].focus();
    } else if (e.key === 'Home') {
      e.preventDefault();
      items[0].focus();
    } else if (e.key === 'End') {
      e.preventDefault();
      items[items.length - 1].focus();
    }
  };

  const handleSignOut = async () => {
    if (signingOut) return;
    setSigningOut(true);
    try {
      await beaconApi().logout();
    } catch {
      // fall through — still navigate to login
    }
    setMenuOpen(false);
    queryClient.setQueryData(['auth', 'me'], null);
    await queryClient.invalidateQueries();
    navigate('/login', { replace: true });
  };

  return (
    <aside className="sticky top-0 h-screen flex flex-col border-r border-border bg-surface">
      <div className="flex items-center justify-between px-4 py-3.5 border-b border-border">
        <div className="flex items-center gap-2 font-semibold tracking-tightish">
          <span className="beacon-logo-dot" aria-hidden="true" />
          <span>Beacon</span>
        </div>
        <BuildBadge />
      </div>

      <nav className="flex-1 overflow-y-auto px-2 py-3 flex flex-col gap-4">
        {SECTIONS.map(section => (
          <div className="flex flex-col gap-0.5" key={section.label}>
            <div className="px-2.5 mb-1 text-2xs font-semibold uppercase tracking-eyebrow text-text-subtle">
              {section.label}
            </div>
            {section.items.map(item => {
              const href = `/${item.slug}`;
              const isActive = location.pathname.startsWith(href);
              const cls = cn(navItemBase, isActive && navItemActive);

              return (
                <Link key={item.name} to={href} className={cls}>
                  <item.Icon className="size-4 shrink-0" />
                  <span className="flex-1 truncate">{item.name}</span>
                  {item.badge && (
                    <span className="text-[9.5px] font-semibold uppercase tracking-eyebrow px-1.5 py-0.5 rounded-xs bg-brand-100 text-brand-700">
                      {item.badge}
                    </span>
                  )}
                  {item.count != null && !item.badge && (
                    <span className="text-2xs mono text-text-subtle">{item.count}</span>
                  )}
                </Link>
              );
            })}
          </div>
        ))}
        <div className="flex flex-col gap-0.5 mt-2">
          <Link to="/about" className={navItemBase}>
            <IconInfo className="size-4 shrink-0" />
            <span>About</span>
          </Link>
        </div>
      </nav>

      <div className="border-t border-border px-2 py-2 relative" ref={menuRef}>
        {menuOpen && (
          <div
            ref={menuPopRef}
            role="menu"
            onKeyDown={onMenuKeyDown}
            className="absolute bottom-full left-2 right-2 mb-1 rounded-sm border border-border bg-surface shadow-lg py-1 z-50"
          >
            <Link
              to="/settings"
              role="menuitem"
              tabIndex={-1}
              onClick={() => setMenuOpen(false)}
              className="flex items-center gap-2 px-2.5 py-1.5 text-sm text-text hover:bg-surface-2"
            >
              <IconCog className="size-4 shrink-0 text-text-muted" />
              <span>Settings</span>
            </Link>
            <button
              type="button"
              role="menuitem"
              tabIndex={-1}
              onClick={handleSignOut}
              disabled={signingOut}
              className="w-full flex items-center gap-2 px-2.5 py-1.5 text-sm text-text hover:bg-surface-2 disabled:opacity-60 text-left"
            >
              <IconLogOut className="size-4 shrink-0 text-text-muted" />
              <span>{signingOut ? 'Signing out…' : 'Sign out'}</span>
            </button>
          </div>
        )}
        <button
          ref={triggerRef}
          type="button"
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen(o => !o)}
          className="w-full flex items-center gap-2.5 px-2 py-1.5 rounded-sm hover:bg-surface-2 text-left"
        >
          <div className="size-7 rounded-sm bg-brand-100 text-brand-700 grid place-items-center text-2xs font-semibold">
            {userInitials(user?.displayName ?? user?.email)}
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-sm font-medium truncate">
              {user?.displayName ?? user?.email ?? '—'}
            </div>
            <div className="text-2xs text-text-subtle truncate">
              {user?.roles && user.roles.length > 0 ? user.roles.join(' · ') : 'Authenticated'}
            </div>
          </div>
          <ChevronDown className={cn('size-3.5 text-text-subtle transition-transform', menuOpen && 'rotate-180')} />
        </button>
      </div>
    </aside>
  );
}
