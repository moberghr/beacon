import { Zap } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDate } from '@/lib/format';
import type { ExecutionTimeDataPoint, NotificationStatisticsEntry } from '../queries';

interface ExecutionsTabProps {
  notificationHistory: NotificationStatisticsEntry[];
  executionTimeHistory: ExecutionTimeDataPoint[];
}

/**
 * Per-day rollup of executions, notifications and timing.
 */
export function ExecutionsTab({ notificationHistory, executionTimeHistory }: ExecutionsTabProps) {
  const merged = mergeByDate(notificationHistory, executionTimeHistory);

  if (merged.length === 0) {
    return (
      <div className="p-4">
        <EmptyState
          icon={<Zap size={20} />}
          title="No executions recorded"
          description="Hit Execute query to populate run statistics."
        />
      </div>
    );
  }

  const thClass = 'text-left px-3.5 py-2.5 mono font-semibold uppercase tracking-eyebrow text-text-muted bg-surface-2 border-b border-border whitespace-nowrap';
  const thRight = thClass + ' text-right';
  const tdClass = 'mono px-3.5 py-2.5 border-b border-border';
  const tdRight = tdClass + ' text-right';

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-xs">
        <thead>
          <tr>
            <th className={thClass}>Date</th>
            <th className={thRight}>Executions</th>
            <th className={thRight}>Successful</th>
            <th className={thRight}>Failed</th>
            <th className={thRight}>Success rate</th>
            <th className={thRight}>Avg ms</th>
            <th className={thRight}>Min · Max</th>
          </tr>
        </thead>
        <tbody>
          {merged.map(row => (
            <tr key={row.date} className="hover:bg-surface-2">
              <td className={tdClass}>{formatDate(row.date)}</td>
              <td className={tdRight}>{row.totalExecutions}</td>
              <td className={tdRight}>{row.successfulNotifications}</td>
              <td className={tdRight}>
                {row.failedExecutions > 0
                  ? <span className="text-crit">{row.failedExecutions}</span>
                  : 0}
              </td>
              <td className={tdRight}>
                {row.totalExecutions > 0 ? `${row.successRate.toFixed(1)}%` : '—'}
              </td>
              <td className={tdRight}>
                {row.avgMs == null ? '—' : row.avgMs.toFixed(2)}
              </td>
              <td className={tdRight + ' text-text-subtle'}>
                {row.minMs == null || row.maxMs == null
                  ? '—'
                  : `${row.minMs.toFixed(1)} · ${row.maxMs.toFixed(1)}`}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

interface MergedRow {
  date: string;
  totalExecutions: number;
  successfulNotifications: number;
  failedExecutions: number;
  successRate: number;
  avgMs: number | null;
  minMs: number | null;
  maxMs: number | null;
}

function mergeByDate(
  notif: NotificationStatisticsEntry[],
  timing: ExecutionTimeDataPoint[],
): MergedRow[] {
  const map = new Map<string, MergedRow>();

  for (const n of notif) {
    const key = isoDate(n.date);
    map.set(key, {
      date: key,
      totalExecutions: n.totalExecutions,
      successfulNotifications: n.successfulNotifications,
      failedExecutions: n.failedExecutions,
      successRate: n.successRate,
      avgMs: null,
      minMs: null,
      maxMs: null,
    });
  }

  for (const t of timing) {
    const key = isoDate(t.date);
    const existing = map.get(key);
    if (existing) {
      existing.avgMs = t.avgExecutionTimeMs;
      existing.minMs = t.minExecutionTimeMs;
      existing.maxMs = t.maxExecutionTimeMs;
    } else {
      map.set(key, {
        date: key,
        totalExecutions: 0,
        successfulNotifications: 0,
        failedExecutions: 0,
        successRate: 0,
        avgMs: t.avgExecutionTimeMs,
        minMs: t.minExecutionTimeMs,
        maxMs: t.maxExecutionTimeMs,
      });
    }
  }

  return Array.from(map.values()).sort((a, b) => b.date.localeCompare(a.date));
}

function isoDate(value: string): string {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toISOString().slice(0, 10);
}
