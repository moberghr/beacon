import { AlertTriangle, Clock } from 'lucide-react';
import { Pill } from '@/components/beacon';
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
      <div className="p-4">
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title="Failed to load change history"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  const changes = data?.changes ?? [];

  if (!isLoading && changes.length === 0) {
    return (
      <div className="p-4">
        <EmptyState
          icon={<Clock size={20} />}
          title="No SQL changes recorded"
          description="Edits to this query's SQL will appear here once tracked."
        />
      </div>
    );
  }

  return (
    <div className="overflow-x-auto">
      {isLoading && <div className="p-4"><span className="text-text-muted">Loading…</span></div>}
      {!isLoading && (
        <table className="w-full border-collapse text-xs">
          <thead>
            <tr>
              {['Changed at', 'Step', 'Source', 'Author', 'Reason'].map(h => (
                <th
                  key={h}
                  className="text-left px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border whitespace-nowrap"
                >
                  {h}
                </th>
              ))}
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
    <tr className="hover:bg-surface-2">
      <td className="mono text-text-subtle px-3.5 py-2.5 border-b border-border">{formatDateTime(entry.changedAt)}</td>
      <td className="px-3.5 py-2.5 border-b border-border">
        <span className="mono">#{entry.queryStepOrder}</span>
        <span className="text-text-subtle"> · {entry.queryStepName ?? '—'}</span>
      </td>
      <td className="px-3.5 py-2.5 border-b border-border">
        <Pill tone={entry.changeSource === 2 ? 'info' : 'neutral'}>{sourceLabel}</Pill>
      </td>
      <td className="px-3.5 py-2.5 border-b border-border"><span className="mono">{author}</span></td>
      <td className="text-text-muted px-3.5 py-2.5 border-b border-border max-w-[360px] truncate">
        {entry.changeReason ?? <span className="text-text-subtle">—</span>}
      </td>
    </tr>
  );
}
