import { type ReactNode, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { AlertTriangle, Check, RefreshCw } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, PageHeader, Seg, type SegOption } from '@/components/beacon';
import { useAccess } from '@/auth/useAuth';
import { ChangeApprovalKind } from '@/lib/enums';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import {
  CHANGE_KIND_LABEL,
  useChangeApprovalsList,
  usePendingApprovalsList,
  type ApprovalRequestSummary,
  type ChangeApprovalSummary,
} from './queries';
import { ReviewApprovalDialog } from './ReviewApprovalDialog';

const GRID_TEMPLATE = '0.6fr 1.4fr 1.2fr 1.2fr 2fr 0.8fr';
const CHANGE_GRID_TEMPLATE = '0.6fr 1.6fr 1.2fr 1.2fr 2fr';

/** The kinds of request the list shows: query changes, or one of the other governed changes (by its wire value). */
type KindFilter = 'queries' | `${ChangeApprovalKind}`;

const KIND_OPTIONS: SegOption<KindFilter>[] = [
  { value: 'queries', label: 'Queries' },
  { value: `${ChangeApprovalKind.MigrationJob}`, label: 'Migration jobs' },
  { value: `${ChangeApprovalKind.CustomSqlRules}`, label: 'Custom SQL rules' },
  { value: `${ChangeApprovalKind.RecipientDestination}`, label: 'Recipients' },
  { value: `${ChangeApprovalKind.DataSourceConnection}`, label: 'Data sources' },
  { value: `${ChangeApprovalKind.LlmConnection}`, label: 'LLM connection' },
  { value: `${ChangeApprovalKind.AdminRoleGrant}`, label: 'Admin role' },
];

// Recipient and data-source connection changes are an Approver's to read, and LLM connection changes and Admin role grants only an Admin's: the
// server lists them to nobody else.
const APPROVER_KINDS: KindFilter[] = [`${ChangeApprovalKind.RecipientDestination}`, `${ChangeApprovalKind.DataSourceConnection}`];
const ADMIN_KINDS: KindFilter[] = [`${ChangeApprovalKind.LlmConnection}`, `${ChangeApprovalKind.AdminRoleGrant}`];

function kindOptions(canReview: boolean, isAdmin: boolean): SegOption<KindFilter>[] {
  return KIND_OPTIONS
    .filter(x => canReview || !APPROVER_KINDS.includes(x.value))
    .filter(x => isAdmin || !ADMIN_KINDS.includes(x.value));
}

export default function ApprovalsListPage() {
  const [params, setParams] = useSearchParams();
  // Approving and rejecting is an Approver's (an Admin holds it); everyone else reads the query, migration job and
  // Custom SQL requests of the projects whose data they read, never a recipient's or a data source's. LLM connection
  // changes are read and decided by an Admin only.
  const access = useAccess();
  const canReview = access.hasPermission('Approver');
  const options = kindOptions(canReview, access.isAdmin);
  const requested = params.get('kind') ?? 'queries';
  const kind: KindFilter = options.some(x => x.value === requested) ? (requested as KindFilter) : 'queries';

  const selectKind = (value: KindFilter) => {
    // A new kind starts on its first page, in its default order.
    setParams(value === 'queries' ? {} : { kind: value });
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      {kind === 'queries' ? (
        <QueryApprovals canReview={canReview} filter={<KindSeg options={options} value={kind} onChange={selectKind} />} />
      ) : (
        <ChangeApprovals
          kind={Number(kind) as ChangeApprovalKind}
          filter={<KindSeg options={options} value={kind} onChange={selectKind} />}
        />
      )}
    </div>
  );
}

function KindSeg({
  options,
  value,
  onChange,
}: {
  options: SegOption<KindFilter>[];
  value: KindFilter;
  onChange: (value: KindFilter) => void;
}) {
  return <Seg<KindFilter> ariaLabel="Kind of change" options={options} value={value} onChange={onChange} />;
}

function Header({ count, isLoading, onRefresh }: { count: number; isLoading: boolean; onRefresh: () => void }) {
  return (
    <PageHeader
      variant="pulse"
      eyebrow="Workflow"
      prefix="Pending"
      emphasis="approvals"
      sub={
        isLoading
          ? <span className="text-text-muted">Loading…</span>
          : <span className="text-text-muted">{formatNumber(count)} pending</span>
      }
      actions={
        <Button icon={<RefreshCw />} onClick={onRefresh} disabled={isLoading}>
          Refresh
        </Button>
      }
    />
  );
}

function submittedAt(createdTime: string | null | undefined) {
  return createdTime
    ? <span title={formatDateTime(createdTime)}>{formatRelativeTime(createdTime)}</span>
    : <span className="text-text-muted">—</span>;
}

function QueryApprovals({ canReview, filter }: { canReview: boolean; filter: ReactNode }) {
  const navigate = useNavigate();
  const list = usePendingApprovalsList();
  const { data, isLoading, isError, error, refetch } = list;
  const [reviewing, setReviewing] = useState<number | null>(null);

  // Hub-driven invalidation lives in `useHubInvalidations` mounted in
  // AppShell — approvals refresh automatically when SignalR broadcasts
  // ApprovalUpdated.

  const columns = useMemo<Column<ApprovalRequestSummary>[]>(() => [
    { key: 'id', header: 'Id', sortKey: 'id', render: a => <span className="text-text-muted mono">#{a.id}</span> },
    {
      key: 'query',
      sortKey: 'queryName',
      header: 'Query',
      render: a => <span className="font-semibold text-text">{a.queryName ?? '—'}</span>,
    },
    {
      key: 'submittedBy',
      header: 'Submitted by',
      render: a => a.requestedByUserName ?? <span className="text-text-muted">Unknown</span>,
    },
    { key: 'submittedAt', sortKey: 'createdTime', header: 'Submitted at', render: a => submittedAt(a.createdTime) },
    {
      key: 'summary',
      header: 'Change summary',
      render: a => a.changeSummary
        ? <span className="text-text-muted">{a.changeSummary}</span>
        : <span className="text-text-muted">—</span>,
    },
    {
      key: 'actions',
      header: '',
      render: a => canReview
        ? (
          <Button
            variant="primary"
            size="sm"
            onClick={e => { e.stopPropagation(); setReviewing(a.id); }}
          >
            Review
          </Button>
        )
        : null,
    },
  ], [canReview]);

  return (
    <>
      <Header count={data?.totalCount ?? 0} isLoading={isLoading} onRefresh={() => refetch()} />
      {filter}

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load approvals"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={list.items}
          {...list.tableProps}
          rowKey={a => a.id}
          gridTemplate={GRID_TEMPLATE}
          onRowClick={a => navigate(`/approvals/${a.id}`)}
          empty={
            <EmptyState
              icon={<Check />}
              title={isLoading ? 'Loading approvals…' : 'No pending approvals'}
              description={isLoading ? '' : 'Query change requests will appear here when they are submitted.'}
            />
          }
        />
      )}

      {canReview && (
        <ReviewApprovalDialog
          open={reviewing != null}
          approvalId={reviewing}
          onClose={() => setReviewing(null)}
        />
      )}
    </>
  );
}

function ChangeApprovals({ kind, filter }: { kind: ChangeApprovalKind; filter: ReactNode }) {
  const navigate = useNavigate();
  const list = useChangeApprovalsList(kind);
  const { data, isLoading, isError, error, refetch } = list;
  const label = CHANGE_KIND_LABEL[kind] ?? 'Resource';

  const columns = useMemo<Column<ChangeApprovalSummary>[]>(() => [
    { key: 'id', header: 'Id', sortKey: 'id', render: a => <span className="text-text-muted mono">#{a.id}</span> },
    {
      key: 'resource',
      sortKey: 'resourceName',
      header: label,
      render: a => <span className="font-semibold text-text">{a.resourceName}</span>,
    },
    {
      key: 'submittedBy',
      header: 'Submitted by',
      render: a => a.requestedByUserName ?? <span className="text-text-muted">Unknown</span>,
    },
    { key: 'submittedAt', sortKey: 'createdTime', header: 'Submitted at', render: a => submittedAt(a.createdTime) },
    {
      key: 'summary',
      header: 'Change summary',
      render: a => a.changeSummary
        ? <span className="text-text-muted">{a.changeSummary}</span>
        : <span className="text-text-muted">—</span>,
    },
  ], [label]);

  return (
    <>
      <Header count={data?.totalCount ?? 0} isLoading={isLoading} onRefresh={() => refetch()} />
      {filter}

      {isError && (
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load approvals"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <DataTable
          columns={columns}
          rows={list.items}
          {...list.tableProps}
          rowKey={a => a.id}
          gridTemplate={CHANGE_GRID_TEMPLATE}
          onRowClick={a => navigate(`/approvals/changes/${a.id}`)}
          empty={
            <EmptyState
              icon={<Check />}
              title={isLoading ? 'Loading approvals…' : 'No pending approvals'}
              description={isLoading ? '' : `${label} changes waiting for a second person's approval will appear here.`}
            />
          }
        />
      )}
    </>
  );
}
