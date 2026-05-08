import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDateTime } from '@/lib/format';
import {
  CHANGE_SOURCE_LABEL,
  useQueryChangeHistoryQuery,
  type QueryChangeHistoryEntry,
} from '../queries';

interface ChangeHistoryTabProps {
  queryId: number;
}

export function ChangeHistoryTab({ queryId }: ChangeHistoryTabProps) {
  const { data, isLoading, isError, error } = useQueryChangeHistoryQuery(queryId, {
    maxResults: 50,
  });

  if (isError) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load change history"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const changes = data?.changes ?? [];

  if (!isLoading && changes.length === 0) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Clock size={20} />}
          title="No SQL changes recorded"
          description="Edits to this query's SQL will appear here once tracked."
        />
      </div>
    );
  }

  return (
    <div className="data-tbl">
      {isLoading && <div className="card__body"><span className="muted">Loading…</span></div>}
      {!isLoading && (
        <table>
          <thead>
            <tr>
              <th>Changed at</th>
              <th>Step</th>
              <th>Source</th>
              <th>Author</th>
              <th>Reason</th>
            </tr>
          </thead>
          <tbody>
            {changes.map(c => <ChangeRow key={c.id} entry={c} />)}
          </tbody>
        </table>
      )}
    </div>
  );
}

function ChangeRow({ entry }: { entry: QueryChangeHistoryEntry }) {
  const sourceLabel = CHANGE_SOURCE_LABEL[entry.changeSource] ?? `source ${entry.changeSource}`;
  const author = entry.aiActorName ?? entry.userId ?? 'unknown';

  return (
    <tr>
      <td className="mono subtle">{formatDateTime(entry.changedAt)}</td>
      <td>
        <span className="mono">#{entry.queryStepOrder}</span>
        <span className="subtle"> · {entry.queryStepName ?? '—'}</span>
      </td>
      <td>
        <span className={`pill pill--${entry.changeSource === 2 ? 'info' : 'neutral'}`}>
          {sourceLabel}
        </span>
      </td>
      <td><span className="mono">{author}</span></td>
      <td className="muted" style={{ maxWidth: 360, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {entry.changeReason ?? <span className="subtle">—</span>}
      </td>
    </tr>
  );
}
