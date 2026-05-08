import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { formatDate } from '@/lib/format';
import type { ExecutionTimeDataPoint, NotificationStatisticsEntry } from '../queries';

interface ExecutionsTabProps {
  notificationHistory: NotificationStatisticsEntry[];
  executionTimeHistory: ExecutionTimeDataPoint[];
}

/**
 * Per-day rollup of executions, notifications and timing. Backend
 * `NotificationHistory` and `ExecutionTimeHistory` are independent series
 * keyed by date — we merge them by ISO date string for display.
 */
export function ExecutionsTab({ notificationHistory, executionTimeHistory }: ExecutionsTabProps) {
  const merged = mergeByDate(notificationHistory, executionTimeHistory);

  if (merged.length === 0) {
    return (
      <div className="card__body">
        <EmptyState
          icon={<Icon.Bolt size={20} />}
          title="No executions recorded"
          description="Hit Execute query to populate run statistics."
        />
      </div>
    );
  }

  return (
    <div className="data-tbl">
      <table>
        <thead>
          <tr>
            <th>Date</th>
            <th style={{ textAlign: 'right' }}>Executions</th>
            <th style={{ textAlign: 'right' }}>Successful</th>
            <th style={{ textAlign: 'right' }}>Failed</th>
            <th style={{ textAlign: 'right' }}>Success rate</th>
            <th style={{ textAlign: 'right' }}>Avg ms</th>
            <th style={{ textAlign: 'right' }}>Min · Max</th>
          </tr>
        </thead>
        <tbody>
          {merged.map(row => (
            <tr key={row.date}>
              <td className="mono">{formatDate(row.date)}</td>
              <td className="mono" style={{ textAlign: 'right' }}>{row.totalExecutions}</td>
              <td className="mono" style={{ textAlign: 'right' }}>{row.successfulNotifications}</td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {row.failedExecutions > 0
                  ? <span style={{ color: 'var(--crit)' }}>{row.failedExecutions}</span>
                  : 0}
              </td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {row.totalExecutions > 0 ? `${row.successRate.toFixed(1)}%` : '—'}
              </td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {row.avgMs == null ? '—' : row.avgMs.toFixed(2)}
              </td>
              <td className="mono subtle" style={{ textAlign: 'right' }}>
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
  // Server emits ISO datetimes; collapse to date string for daily key.
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toISOString().slice(0, 10);
}
