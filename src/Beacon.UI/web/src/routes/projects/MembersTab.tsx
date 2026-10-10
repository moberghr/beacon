import { useEffect, useState } from 'react';
import { AlertTriangle, UserPlus, Users, X } from 'lucide-react';
import { Banner, Button, CardBody, Input, Pill, Seg, type SegOption } from '@/components/beacon';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { ProjectRole } from '@/lib/enums';
import { formatRelativeTime } from '@/lib/format';
import {
  MEMBER_SEARCH_MIN_LENGTH,
  PROJECT_ROLE_LABEL,
  useMemberCandidates,
  useProjectMembers,
  useRemoveProjectMember,
  useSetProjectMember,
  userLabel,
  type ProjectMemberEntry,
} from './queries';

const ROLES = [ProjectRole.Viewer, ProjectRole.Editor, ProjectRole.ProjectAdmin];

// Seg works on strings; the role travels as its enum value.
const ROLE_OPTIONS: SegOption<string>[] = ROLES.map(x => ({ value: String(x), label: PROJECT_ROLE_LABEL[x] }));

const toRole = (value: string) => Number(value) as ProjectRole;

/**
 * A project's members: who has which role, adding users found by display name, changing roles and removing members. Shown
 * only to a caller who manages the project's members while Beacon (not the host) manages membership.
 */
export function MembersTab({ projectId }: { projectId: number }) {
  const members = useProjectMembers(projectId);
  const setMember = useSetProjectMember(projectId);
  const removeMember = useRemoveProjectMember(projectId);
  const [removing, setRemoving] = useState<ProjectMemberEntry | null>(null);

  const rows = members.items;
  const allOnOnePage = (members.data?.totalCount ?? 0) <= rows.length;
  const hasActiveProjectAdmin = rows.some(x => x.isActive && x.role === ProjectRole.ProjectAdmin);

  const columns: Column<ProjectMemberEntry>[] = [
    {
      key: 'displayName',
      sortKey: 'displayName',
      header: 'Member',
      render: r => (
        <span className="flex items-center gap-2">
          <span className="font-semibold text-text">{userLabel(r.userId, r.displayName)}</span>
          {!r.isActive && <Pill title="Disabled or archived: this membership grants nothing">Inactive</Pill>}
        </span>
      ),
    },
    {
      key: 'role',
      sortKey: 'role',
      header: 'Role',
      render: r => (
        <Seg
          ariaLabel={`Role of ${userLabel(r.userId, r.displayName)}`}
          options={ROLE_OPTIONS}
          value={String(r.role)}
          onChange={value => {
            if (toRole(value) !== r.role) {
              setMember.mutate({ userId: r.userId, role: toRole(value) });
            }
          }}
        />
      ),
    },
    {
      key: 'addedAt',
      sortKey: 'addedAt',
      header: 'Added',
      render: r => <span className="text-text-muted">{formatRelativeTime(r.addedAt)}</span>,
    },
    {
      key: 'actions',
      header: '',
      render: r => (
        <Button
          variant="ghost"
          size="sm"
          aria-label={`Remove ${userLabel(r.userId, r.displayName)}`}
          title="Remove member"
          onClick={() => setRemoving(r)}
          icon={<X />}
        />
      ),
    },
  ];

  const onConfirmRemove = async () => {
    if (removing == null) return;
    try {
      await removeMember.mutateAsync(removing.userId);
      setRemoving(null);
    } catch {
      // Error toast already raised by the remove mutation hook.
    }
  };

  return (
    <>
      <CardBody className="flex flex-col gap-4">
        <AddMember projectId={projectId} />

        {members.isError && (
          <EmptyState
            icon={<AlertTriangle />}
            title="Failed to load members"
            description={members.error instanceof Error ? members.error.message : 'Unknown error'}
          />
        )}

        {!members.isLoading && !members.isError && allOnOnePage && !hasActiveProjectAdmin && (
          <Banner
            tone="warn"
            icon={<AlertTriangle />}
            title="This project has no active project admin"
            sub="Only Admins can manage its members and link data sources until someone is made project admin."
          />
        )}
      </CardBody>

      {!members.isError && (
        <CardBody flush>
          <DataTable
            columns={columns}
            rows={rows}
            {...members.tableProps}
            rowKey={r => r.userId}
            gridTemplate="1.6fr 2fr 0.9fr 60px"
            ariaLabel="Project members"
            empty={
              <EmptyState
                icon={<Users />}
                title={members.isLoading ? 'Loading members…' : 'No members yet'}
                description={members.isLoading ? '' : 'Add people by display name above. Once the project is restricted, only its members and Admins can use it.'}
              />
            }
          />
        </CardBody>
      )}

      <ConfirmDialog
        open={removing != null}
        title="Remove member"
        message={
          removing
            ? <>Remove <strong>{userLabel(removing.userId, removing.displayName)}</strong> from this project? If the project is restricted, they lose access to it.</>
            : ''
        }
        confirmLabel="Remove"
        destructive
        busy={removeMember.isPending}
        onConfirm={onConfirmRemove}
        onCancel={() => setRemoving(null)}
      />
    </>
  );
}

/** Search users by display name (two characters at least) and add one with the chosen role. */
function AddMember({ projectId }: { projectId: number }) {
  const [term, setTerm] = useState('');
  const [debounced, setDebounced] = useState('');
  const [role, setRole] = useState<ProjectRole>(ProjectRole.Viewer);
  const setMember = useSetProjectMember(projectId);

  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(term.trim()), 300);
    return () => window.clearTimeout(handle);
  }, [term]);

  const candidates = useMemberCandidates(projectId, debounced);
  const tooShort = debounced.length < MEMBER_SEARCH_MIN_LENGTH;
  const items = candidates.data?.candidates ?? [];

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <Input
          type="search"
          value={term}
          onChange={e => setTerm(e.target.value)}
          placeholder="Add a member: search by display name…"
          aria-label="Search people to add"
          className="max-w-[320px]"
        />
        <span className="text-xs text-text-muted">as</span>
        <Seg ariaLabel="Role for the new member" options={ROLE_OPTIONS} value={String(role)} onChange={x => setRole(toRole(x))} />
      </div>

      {term.trim().length > 0 && tooShort
        ? <span className="text-xs text-text-muted">Type at least {MEMBER_SEARCH_MIN_LENGTH} characters to search.</span>
        : <span className="text-xs text-text-subtle">The search matches display names only; people without one are not found here.</span>}

      {!tooShort && (
        <div className="border border-border rounded-sm max-h-60 overflow-auto" aria-label="People to add">
          {candidates.isLoading && <div className="text-text-muted p-3 text-sm">Searching…</div>}
          {candidates.isError && <div className="text-crit p-3 text-sm">The search failed. Try again in a moment.</div>}
          {!candidates.isLoading && !candidates.isError && items.length === 0 && (
            <div className="text-text-muted p-3 text-sm">No display name matches "{debounced}".</div>
          )}
          {items.map(x => (
            <div key={x.userId} className="flex items-center justify-between gap-2 px-3 py-2 text-sm border-b border-border last:border-b-0">
              <span className="truncate">{userLabel(x.userId, x.displayName)}</span>
              {x.isMember
                ? <span className="text-xs text-text-muted">Already a member</span>
                : (
                  <Button
                    size="sm"
                    icon={<UserPlus />}
                    aria-label={`Add ${userLabel(x.userId, x.displayName)}`}
                    disabled={setMember.isPending}
                    onClick={() => setMember.mutate({ userId: x.userId, role })}
                  >
                    Add
                  </Button>
                )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
