import { useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { useHubEvent } from '@/lib/useHubEvent';
import { formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { usePendingApprovalsQuery, type ApprovalRequestSummary } from './queries';
import { ReviewApprovalDialog } from './ReviewApprovalDialog';

const GRID_TEMPLATE = '0.6fr 1.4fr 1.2fr 1.2fr 2fr 0.8fr';

export default function ApprovalsListPage() {
  const { data, isLoading, isError, error, refetch } = usePendingApprovalsQuery();
  const [reviewing, setReviewing] = useState<number | null>(null);
  const qc = useQueryClient();

  // Realtime: invalidate the list whenever the server announces an approval
  // change (handled by another reviewer, or this one in another tab).
  useHubEvent('ApprovalUpdated', () => {
    qc.invalidateQueries({ queryKey: ['approvals'] });
  });

  const entries = data ?? [];

  const columns = useMemo<Column<ApprovalRequestSummary>[]>(() => [
    { key: 'id', header: 'Id', render: a => <span className="muted mono">#{a.id}</span> },
    {
      key: 'query',
      header: 'Query',
      render: a => <span style={{ fontWeight: 600, color: 'var(--text)' }}>{a.queryName ?? '—'}</span>,
    },
    {
      key: 'submittedBy',
      header: 'Submitted by',
      render: a => a.requestedByUserName ?? <span className="muted">Unknown</span>,
    },
    {
      key: 'submittedAt',
      header: 'Submitted at',
      render: a => a.createdTime
        ? <span title={formatDateTime(a.createdTime as unknown as string)}>{formatRelativeTime(a.createdTime as unknown as string)}</span>
        : <span className="muted">—</span>,
    },
    {
      key: 'summary',
      header: 'Change summary',
      render: a => a.changeSummary
        ? <span className="muted">{a.changeSummary}</span>
        : <span className="muted">—</span>,
    },
    {
      key: 'actions',
      header: '',
      render: a => (
        <button
          type="button"
          className="btn btn--primary"
          onClick={e => { e.stopPropagation(); setReviewing(a.id ?? null); }}
        >
          Review
        </button>
      ),
    },
  ], []);

  return (
    <div className="page">
      <PageHeader
        title="Pending approvals"
        sub={
          isLoading
            ? <span className="muted">Loading…</span>
            : <span className="muted">{formatNumber(entries.length)} pending</span>
        }
        actions={
          <button className="btn" type="button" onClick={() => refetch()} disabled={isLoading}>
            <Icon.Refresh size={14} className="btn__icon" />
            Refresh
          </button>
        }
      />

      {isError && (
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load approvals"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      )}

      {!isError && (
        <div className="card" style={{ padding: 0 }}>
          <DataTable
            columns={columns}
            rows={entries}
            rowKey={a => a.id ?? 0}
            gridTemplate={GRID_TEMPLATE}
            empty={
              <EmptyState
                icon={<Icon.Check size={20} />}
                title={isLoading ? 'Loading approvals…' : 'No pending approvals'}
                description={isLoading ? '' : 'Query change requests will appear here when they are submitted.'}
              />
            }
          />
        </div>
      )}

      <ReviewApprovalDialog
        open={reviewing != null}
        approvalId={reviewing}
        onClose={() => setReviewing(null)}
      />
    </div>
  );
}
