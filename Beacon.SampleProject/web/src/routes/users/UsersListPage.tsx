import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ApiError } from '@/lib/api';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useToggleUserEnabled, useUsersQuery, type UserEntry } from './queries';
import { UserDialog } from './UserDialog';

const GRID_TEMPLATE = '0.5fr 1.2fr 1.4fr 1fr 0.8fr 1.4fr 0.7fr 1.2fr';

function roleLevelClass(level: number): string {
  if (level >= 3) return 'pill pill--crit';
  if (level === 2) return 'pill pill--warn';
  if (level === 1) return 'pill pill--info';
  return 'pill';
}

export default function UsersListPage() {
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<UserEntry | null | undefined>(undefined);
  const usersQuery = useUsersQuery(search);
  const toggle = useToggleUserEnabled();

  const entries = usersQuery.data?.entries ?? [];

  const handleToggle = async (user: UserEntry) => {
    try {
      await toggle.mutateAsync(user.id);
      toast.success(`${user.userName} is now ${user.isEnabled ? 'disabled' : 'enabled'}`);
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Toggle failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  };

  const columns = useMemo<Column<UserEntry>[]>(() => [
    { key: 'id', header: 'Id', render: u => <span className="muted mono">#{u.id}</span> },
    {
      key: 'userName',
      header: 'Username',
      render: u => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{u.userName}</span>,
    },
    { key: 'email', header: 'Email', render: u => u.email ?? <span className="muted">—</span> },
    { key: 'displayName', header: 'Display name', render: u => u.displayName ?? <span className="muted">—</span> },
    {
      key: 'type',
      header: 'Type',
      render: u => u.isInternalUser
        ? <span className="pill pill--info">Internal</span>
        : <span className="pill">External</span>,
    },
    {
      key: 'roles',
      header: 'Roles',
      render: u => (
        <span style={{ display: 'inline-flex', gap: 4, flexWrap: 'wrap' }}>
          {u.roles.map(r => <span key={r.id} className={roleLevelClass(r.level)}>{r.name}</span>)}
          {u.isSuperAdmin && <span className="pill pill--crit">Super Admin</span>}
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      render: u => (
        <button
          type="button"
          className={u.isEnabled ? 'pill pill--ok' : 'pill pill--crit'}
          style={{ border: 'none', cursor: u.isSuperAdmin ? 'not-allowed' : 'pointer' }}
          disabled={u.isSuperAdmin || toggle.isPending}
          onClick={e => { e.stopPropagation(); handleToggle(u); }}
          title={u.isSuperAdmin ? 'Cannot toggle a super admin' : 'Click to toggle'}
        >
          {u.isEnabled ? 'Enabled' : 'Disabled'}
        </button>
      ),
    },
    {
      key: 'lastLogin',
      header: 'Last login',
      render: u => u.lastLoginAt ? formatDateTime(u.lastLoginAt) : <span className="muted">Never</span>,
    },
  ], [toggle.isPending]);

  return (
    <div className="page">
      <PageHeader
        title="User management"
        sub={
          usersQuery.isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} user{entries.length === 1 ? '' : 's'}</span>
        }
        actions={
          <>
            <button className="btn" type="button" onClick={() => usersQuery.refetch()} disabled={usersQuery.isLoading}>
              <Icon.Refresh size={14} className="btn__icon" />
              Refresh
            </button>
            <button className="btn btn--primary" type="button" onClick={() => setEditing(null)}>
              <Icon.Plus size={14} className="btn__icon" />
              Add user
            </button>
          </>
        }
      />

      <div className="card" style={{ padding: 12, marginBottom: 12 }}>
        <input
          type="search"
          className="q-input"
          placeholder="Search by username, email, or display name"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
      </div>

      {usersQuery.isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load users"
          description={usersQuery.error instanceof Error ? usersQuery.error.message : 'Unknown error'}
        />
      )}

      {!usersQuery.isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={u => u.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={u => setEditing(u)}
            empty={
              <EmptyState
                icon={<Icon.Users size={20} />}
                title={usersQuery.isLoading ? 'Loading users…' : 'No users yet'}
                description={usersQuery.isLoading ? '' : 'Add a user to grant access to Beacon.'}
              />
            }
          />
        </div>
      )}

      <UserDialog
        open={editing !== undefined}
        user={editing ?? null}
        onClose={() => setEditing(undefined)}
      />
    </div>
  );
}
