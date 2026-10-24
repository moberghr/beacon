// Beacon sidebar — Tailwind + Beacon design system.
import { useEffect, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  Home as IconHome,
  Radio as IconTower,
  Database as IconDatabase,
  Folder as IconFolder,
  FolderTree as IconFolderTree,
  BookOpen as IconBook,
  Shield as IconShield,
  ArrowLeftRight as IconArrowsLR,
  Bot as IconBot,
  Search as IconQuery,
  Inbox as IconInbox,
  Bell as IconBell,
  Users as IconUsers,
  Check as IconCheck,
  ClipboardCheck as IconApprovals,
  Key as IconKey,
  SlidersHorizontal as IconSliders,
  Wand2 as IconWand,
  Lightbulb as IconLightbulb,
  Settings as IconCog,
  Info as IconInfo,
  ScrollText as IconAudit,
  Activity as IconMcpAudit,
  AppWindow as IconEmbed,
  ChevronDown,
  LogOut as IconLogOut,
  type LucideIcon,
} from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { cn } from '@/lib/utils';
import { useAccess, useAuth, type Access, type BeaconPermission } from '@/auth/useAuth';
import { beaconApi } from '@/api/client';
import { BuildBadge } from '@/components/beacon';
import { usePendingPlanCount } from '@/routes/ai-actors/queries';

interface NavItem {
  name: string;
  Icon: LucideIcon;
  slug: string;
  count?: number;
  badge?: string;
  /** Who sees the item; omitted: everyone who reads project data. */
  visible?: (access: Access) => boolean;
}

/** Pages of project data: hidden only from a user whose roles are functional only (they would answer 403). */
const readsProjectData = (access: Access) => !access.isFunctionalOnly;

/** Pages a functional permission opens (an Admin holds them all); hidden until the permissions are known. */
const opensFor = (...permissions: BeaconPermission[]) => (access: Access) => access.hasAnyPermission(...permissions);

interface NavSection {
  label: string;
  items: NavItem[];
}

/** The pages the MCP gateway's admin UI serves (everything else answers 404 there). */
const GATEWAY_SLUGS = ['projects', 'mcp-settings', 'mcp-learning', 'mcp-audit'];

const SECTIONS: NavSection[] = [
  {
    label: 'Overview',
    items: [
      // Shown to everyone: for a user with functional roles only it explains what they need to see project data.
      { name: 'Home', Icon: IconHome, slug: 'home', visible: () => true },
      { name: 'Control Tower', Icon: IconTower, slug: 'control-tower' },
    ],
  },
  {
    label: 'Data',
    items: [
      {
        name: 'Data Sources',
        Icon: IconDatabase,
        slug: 'data-sources',
        visible: access => readsProjectData(access) || access.hasPermission('DataSourceAdmin'),
      },
      { name: 'Projects', Icon: IconFolder, slug: 'projects' },
      { name: 'Data Quality', Icon: IconShield, slug: 'data-quality' },
      { name: 'Data Migration', Icon: IconArrowsLR, slug: 'migration-jobs', visible: access => access.opensMigrations },
      { name: 'AI Actors', Icon: IconBot, slug: 'ai-actors' },
    ],
  },
  {
    label: 'Alerts',
    items: [
      { name: 'Queries', Icon: IconQuery, slug: 'queries' },
      { name: 'Query Folders', Icon: IconFolderTree, slug: 'query-folders' },
      { name: 'Subscriptions', Icon: IconInbox, slug: 'subscriptions' },
      { name: 'Notifications', Icon: IconBell, slug: 'notifications' },
      { name: 'Recipients', Icon: IconUsers, slug: 'recipients' },
      { name: 'Tasks', Icon: IconCheck, slug: 'tasks' },
    ],
  },
  {
    label: 'MCP',
    items: [
      {
        name: 'API Keys',
        Icon: IconKey,
        slug: 'api-keys',
        visible: access => readsProjectData(access) || access.hasAnyPermission('Auditor', 'UserAdmin'),
      },
      { name: 'MCP Settings', Icon: IconSliders, slug: 'mcp-settings', visible: opensFor('Auditor', 'SecurityAdmin') },
      { name: 'MCP Playground', Icon: IconWand, slug: 'mcp-playground' },
      { name: 'MCP Learning', Icon: IconLightbulb, slug: 'mcp-learning' },
      { name: 'Glossary', Icon: IconBook, slug: 'glossary', visible: access => access.isAdmin },
      { name: 'MCP Audit', Icon: IconMcpAudit, slug: 'mcp-audit', visible: opensFor('Auditor') },
    ],
  },
  {
    label: 'Admin',
    items: [
      { name: 'Approvals', Icon: IconApprovals, slug: 'approvals', visible: opensFor('Approver') },
      { name: 'User Management', Icon: IconUsers, slug: 'users', visible: opensFor('UserAdmin') },
      { name: 'Admin Settings', Icon: IconCog, slug: 'admin-settings', visible: opensFor('Auditor', 'SecurityAdmin') },
      { name: 'Embeds', Icon: IconEmbed, slug: 'embeds', visible: access => access.opensEmbeds },
      { name: 'Security Audit', Icon: IconAudit, slug: 'security-audit', visible: opensFor('Auditor') },
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
  const access = useAccess();
  // The AI actor plans waiting for this user's decision: a badge on AI Actors (the gateway serves no actors).
  const pendingPlans = usePendingPlanCount(!access.isLoading && !access.gatewayMode && readsProjectData(access));
  const attention: Record<string, { count: number; label: string } | undefined> = {
    'ai-actors': pendingPlans.data
      ? { count: pendingPlans.data, label: `${pendingPlans.data} AI actor ${pendingPlans.data === 1 ? 'plan' : 'plans'} pending in the projects you may decide for` }
      : undefined,
  };
  const sections = SECTIONS
    .map(section => ({ ...section, items: section.items.filter(item => (!access.gatewayMode || GATEWAY_SLUGS.includes(item.slug)) && (item.visible ?? readsProjectData)(access)) }))
    .filter(section => section.items.length > 0);
  // The MCP gateway serves no user settings, and a host that owns sign-in also owns sign-out: with neither, no menu.
  const offersSettings = !access.gatewayMode;
  const hasMenu = offersSettings || !user?.externalLogin;
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

  // "Everywhere" also ends every other session of this user (a new security stamp on the server). When the server could
  // not do that it signs nothing out (409): say so, and stay signed in.
  const handleSignOut = async (everywhere: boolean) => {
    if (signingOut) return;
    setSigningOut(true);
    try {
      await beaconApi().logout(everywhere ? true : undefined);
    } catch {
      if (everywhere) {
        setSigningOut(false);
        toast.error('Your other sessions could not be signed out. You are still signed in here.');
        return;
      }
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
          <img src={`${import.meta.env.BASE_URL}beacon-mark.svg`} alt="" aria-hidden="true" className="w-[22px] h-[22px]" />
          <span>Beacon</span>
        </div>
        <BuildBadge />
      </div>

      <nav className="flex-1 overflow-y-auto px-2 py-3 flex flex-col gap-4">
        {sections.map(section => (
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
                  {attention[item.slug] && !item.badge && (
                    <span
                      aria-label={attention[item.slug]!.label}
                      title={attention[item.slug]!.label}
                      className="text-2xs mono font-semibold px-1.5 py-0.5 rounded-xs bg-warn-bg text-warn"
                    >
                      {attention[item.slug]!.count}
                    </span>
                  )}
                  {item.count != null && !item.badge && !attention[item.slug] && (
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
        {menuOpen && hasMenu && (
          <div
            ref={menuPopRef}
            role="menu"
            onKeyDown={onMenuKeyDown}
            className="absolute bottom-full left-2 right-2 mb-1 rounded-sm border border-border bg-surface shadow-lg py-1 z-50"
          >
            {offersSettings && (
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
            )}
            {/* A host that owns sign-in also owns sign-out; Beacon's would only clear its own cookie. */}
            {!user?.externalLogin && (
              <button
                type="button"
                role="menuitem"
                tabIndex={-1}
                onClick={() => handleSignOut(false)}
                disabled={signingOut}
                className="w-full flex items-center gap-2 px-2.5 py-1.5 text-sm text-text hover:bg-surface-2 disabled:opacity-60 text-left"
              >
                <IconLogOut className="size-4 shrink-0 text-text-muted" />
                <span>{signingOut ? 'Signing out…' : 'Sign out'}</span>
              </button>
            )}
            {!user?.externalLogin && user?.signOutEverywhereEnabled && (
              <button
                type="button"
                role="menuitem"
                tabIndex={-1}
                onClick={() => handleSignOut(true)}
                disabled={signingOut}
                title="Also ends your sessions in every other browser and device."
                className="w-full flex items-center gap-2 px-2.5 py-1.5 text-sm text-text hover:bg-surface-2 disabled:opacity-60 text-left"
              >
                <IconLogOut className="size-4 shrink-0 text-text-muted" />
                <span>Sign out everywhere</span>
              </button>
            )}
          </div>
        )}
        <button
          ref={triggerRef}
          type="button"
          aria-haspopup={hasMenu ? 'menu' : undefined}
          aria-expanded={hasMenu ? menuOpen : undefined}
          onClick={() => hasMenu && setMenuOpen(o => !o)}
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
          {hasMenu && <ChevronDown className={cn('size-3.5 text-text-subtle transition-transform', menuOpen && 'rotate-180')} />}
        </button>
      </div>
    </aside>
  );
}
