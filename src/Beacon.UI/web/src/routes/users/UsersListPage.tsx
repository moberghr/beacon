import { useMemo, useState } from 'react';
import { toast } from 'sonner';
import { RefreshCw, Plus, Users, AlertTriangle } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, Pill, Card, CardBody, Input, PageHeader } from '@/components/beacon';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useToggleUserEnabled, useUsersQuery, type UserEntry } from './queries';
import { UserDialog } from './UserDialog';

const GRID_TEMPLATE = '0.5fr 1.2fr 1.4fr 1fr 0.8fr 1.4fr 0.7fr 1.2fr';

function roleLevelTone(level: number): 'crit' | 'warn' | 'info' | 'neutral' {
  if (level >= 3) return 'crit';
  if (level === 2) return 'warn';
  if (level === 1) return 'info';
  return 'neutral';
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
    } catch {
      // createSimpleMutation already surfaced the error toast.
    }
  };

  const columns = useMemo<Column<UserEntry>[]>(() => [
    { key: 'id', header: 'Id', render: u => <span className="text-text-muted mono">#{u.id}</span> },
    {
      key: 'userName',
      header: 'Username',
      render: u => <span className="font-semibold text-text">{u.userName}</span>,
    },
    { key: 'email', header: 'Email', render: u => u.email ?? <span className="text-text-muted">—</span> },
    { key: 'displayName', header: 'Display name', render: u => u.displayName ?? <span className="text-text-muted">—</span> },
    {
      key: 'type',
      header: 'Type',
      render: u => u.isInternalUser
        ? <Pill tone="info">Internal</Pill>
        : <Pill>External</Pill>,
    },
    {
      key: 'roles',
      header: 'Roles',
      render: u => (
        <span className="inline-flex gap-1 flex-wrap">
          {u.roles.map(r => <Pill key={r.id} tone={roleLevelTone(r.level)}>{r.name}</Pill>)}
          {u.isSuperAdmin && <Pill tone="crit">Super Admin</Pill>}
        </span>
      ),
    },
    {
      key: 'status',
      header: 'Status',
      render: u => (
        <button
          type="button"
          className="cursor-pointer disabled:cursor-not-allowed border-0 bg-transparent p-0"
          disabled={u.isSuperAdmin || toggle.isPending}
          onClick={e => { e.stopPropagation(); handleToggle(u); }}
          title={u.isSuperAdmin ? 'Cannot toggle a super admin' : 'Click to toggle'}
        >
          <Pill tone={u.isEnabled ? 'ok' : 'crit'}>{u.isEnabled ? 'Enabled' : 'Disabled'}</Pill>
        </button>
      ),
    },
    {
      key: 'lastLogin',
      header: 'Last login',
      render: u => u.lastLoginAt ? formatDateTime(u.lastLoginAt) : <span className="text-text-muted">Never</span>,
    },
  ], [toggle.isPending]);

  return (
    <div className="flex flex-col gap-3 p-7">
      <PageHeader
        eyebrow="Access"
        prefix="User"
        emphasis="management"
        sub={
          usersQuery.isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} user{entries.length === 1 ? '' : 's'}</span>
        }
        actions={
          <>
            <Button icon={<RefreshCw />} type="button" onClick={() => usersQuery.refetch()} disabled={usersQuery.isLoading}>
              Refresh
            </Button>
            <Button variant="primary" icon={<Plus />} type="button" onClick={() => setEditing(null)}>
              Add user
            </Button>
          </>
        }
      />

      <Card>
        <CardBody>
          <Input
            type="search"
            placeholder="Search by username, email, or display name"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
        </CardBody>
      </Card>

      {usersQuery.isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load users"
          description={usersQuery.error instanceof Error ? usersQuery.error.message : 'Unknown error'}
        />
      )}

      {!usersQuery.isError && (
        <Card>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={u => u.id}
            gridTemplate={GRID_TEMPLATE}
            onRowClick={u => setEditing(u)}
            empty={
              <EmptyState
                icon={<Users />}
                title={usersQuery.isLoading ? 'Loading users…' : 'No users yet'}
                description={usersQuery.isLoading ? '' : 'Add a user to grant access to Beacon.'}
              />
            }
          />
        </Card>
      )}

      <UserDialog
        open={editing !== undefined}
        user={editing ?? null}
        onClose={() => setEditing(undefined)}
      />
    </div>
  );
}
