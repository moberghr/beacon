import { Link } from 'react-router-dom';
import { ArrowLeftRight, Check } from 'lucide-react';
import {
  Card,
  CardHead,
  CardTitle,
  CardSub,
  CardActions,
  CardBody,
} from '@/components/beacon';
import { formatNumber } from '@/lib/format';
import { Mini } from './atoms';
import { useHomeMigrationSummaryQuery, useHomeTaskSummaryQuery } from './queries';

/**
 * Migration overview card — totals, success rate, execution count, errors.
 * Self-contained: owns its own query hook and renders four Mini bars.
 */
export function MigrationOverviewCard() {
  const { data, isLoading } = useHomeMigrationSummaryQuery();

  const total = data?.total ?? 0;
  const successful = data?.successful ?? 0;
  const executions = data?.executions ?? 0;
  const errored = data?.errored ?? 0;
  const successPct = total > 0 ? Math.round((successful / total) * 100) + '%' : '0%';
  const execPct =
    total > 0 ? Math.min(100, Math.round((executions / Math.max(total * 10, 1)) * 100)) + '%' : '0%';
  const errorPct = total > 0 ? Math.round((errored / total) * 100) + '%' : '0%';

  return (
    <Card>
      <CardHead>
        <ArrowLeftRight className="size-3.5 text-text-muted" />
        <CardTitle>Data migration</CardTitle>
        <CardSub>overview</CardSub>
        <CardActions>
          <Link
            to="/migration-history"
            className="text-xs font-medium text-brand-600 hover:underline"
          >
            Open jobs →
          </Link>
        </CardActions>
      </CardHead>
      <CardBody flush>
        <div className="grid grid-cols-2 sm:grid-cols-4">
          <Mini color="var(--info)" label="Total jobs" value={isLoading ? '—' : formatNumber(total)} bar="100%" />
          <Mini color="var(--ok)" label="Successful" value={isLoading ? '—' : formatNumber(successful)} bar={successPct} />
          <Mini
            color="var(--brand-500)"
            label="Executions"
            value={isLoading ? '—' : formatNumber(executions)}
            bar={execPct}
          />
          <Mini color="var(--crit)" label="Errored" value={isLoading ? '—' : formatNumber(errored)} bar={errorPct} />
        </div>
      </CardBody>
    </Card>
  );
}

/**
 * Task management overview card — total / open / resolved.
 */
export function TaskMgmtCard() {
  const { data, isLoading } = useHomeTaskSummaryQuery();

  const total = data?.total ?? 0;
  const open = data?.open ?? 0;
  const resolved = data?.resolved ?? 0;
  const openPct = total > 0 ? Math.round((open / total) * 100) + '%' : '0%';
  const resolvedPct = total > 0 ? Math.round((resolved / total) * 100) + '%' : '0%';

  return (
    <Card>
      <CardHead>
        <Check className="size-3.5 text-text-muted" />
        <CardTitle>Task management</CardTitle>
        <CardSub>{isLoading ? '—' : `${open} unresolved`}</CardSub>
      </CardHead>
      <CardBody flush>
        <div className="grid grid-cols-3">
          <Mini color="var(--text-muted)" label="Total" value={isLoading ? '—' : formatNumber(total)} bar="100%" />
          <Mini color="var(--warn)" label="Open" value={isLoading ? '—' : formatNumber(open)} bar={openPct} />
          <Mini color="var(--ok)" label="Resolved" value={isLoading ? '—' : formatNumber(resolved)} bar={resolvedPct} />
        </div>
      </CardBody>
    </Card>
  );
}
