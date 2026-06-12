import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, Check, RefreshCw } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, PageHeader } from '@/components/beacon';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { usePendingApprovalsQuery, type ApprovalRequestSummary } from './queries';
import { ReviewApprovalDialog } from './ReviewApprovalDialog';

const GRID_TEMPLATE = '0.6fr 1.4fr 1.2fr 1.2fr 2fr 0.8fr';

export default function ApprovalsListPage() {
  const navigate = useNavigate();
  const { data, isLoading, isError, error, refetch } = usePendingApprovalsQuery();
  const [reviewing, setReviewing] = useState<number | null>(null);

  // Hub-driven invalidation lives in `useHubInvalidations` mounted in
  // AppShell — approvals refresh automatically when SignalR broadcasts
  // ApprovalUpdated.

  const entries = data ?? [];

  const columns = useMemo<Column<ApprovalRequestSummary>[]>(() => [
    { key: 'id', header: 'Id', render: a => <span className="text-text-muted mono">#{a.id}</span> },
    {
      key: 'query',
      header: 'Query',
      render: a => <span className="font-semibold text-text">{a.queryName ?? '—'}</span>,
    },
    {
      key: 'submittedBy',
      header: 'Submitted by',
      render: a => a.requestedByUserName ?? <span className="text-text-muted">Unknown</span>,
    },
    {
      key: 'submittedAt',
      header: 'Submitted at',
      render: a => a.createdTime
        ? <span title={formatDateTime(a.createdTime)}>{formatRelativeTime(a.createdTime)}</span>
        : <span className="text-text-muted">—</span>,
    },
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
      render: a => (
        <Button
          variant="primary"
          size="sm"
          onClick={e => { e.stopPropagation(); setReviewing(a.id); }}
        >
          Review
        </Button>
      ),
    },
  ], []);

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="pulse"
        eyebrow="Workflow"
        prefix="Pending"
        emphasis="approvals"
        sub={
          isLoading
            ? <span className="text-text-muted">Loading…</span>
            : <span className="text-text-muted">{formatNumber(entries.length)} pending</span>
        }
        actions={
          <Button icon={<RefreshCw />} onClick={() => refetch()} disabled={isLoading}>
            Refresh
          </Button>
        }
      />

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
          rows={entries}
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

      <ReviewApprovalDialog
        open={reviewing != null}
        approvalId={reviewing}
        onClose={() => setReviewing(null)}
      />
    </div>
  );
}
