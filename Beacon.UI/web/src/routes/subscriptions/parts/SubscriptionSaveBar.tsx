import { RefreshCw, Zap } from 'lucide-react';
import { Button, Pill } from '@/components/beacon';
import type { SubscriptionDetail } from '../queries';

interface SubscriptionSaveBarProps {
  subscription: SubscriptionDetail;
  totalExecutions: number;
  canTest: boolean;
  canArchive: boolean;
  isTesting: boolean;
  isArchiving: boolean;
  onTest: () => void;
  onArchive: () => void;
}

export function SubscriptionSaveBar({
  subscription,
  totalExecutions,
  canTest,
  canArchive,
  isTesting,
  isArchiving,
  onTest,
  onArchive,
}: SubscriptionSaveBarProps) {
  const isActive = subscription.status === 'Active';
  return (
    <div className="flex items-center gap-2 px-5 py-3 border-t border-border bg-surface-2 rounded-md">
      <span className="flex items-center gap-2 text-xs text-text-muted">
        {isActive ? (
          <Pill tone="ok" dot>ACTIVE</Pill>
        ) : (
          <Pill dot>ARCHIVED</Pill>
        )}
        <span>
          <span className="mono">#{subscription.id}</span>
          {' · '}{totalExecutions.toLocaleString()} run{totalExecutions === 1 ? '' : 's'}
          {' · '}{subscription.recipients.length} recipient{subscription.recipients.length === 1 ? '' : 's'}
        </span>
      </span>
      <div className="ml-auto flex items-center gap-1.5">
        <Button
          icon={<RefreshCw />}
          onClick={onArchive}
          disabled={!canArchive || !isActive || isArchiving}
        >
          {isArchiving ? 'Archiving…' : 'Archive'}
        </Button>
        <Button
          variant="primary"
          icon={<Zap />}
          onClick={onTest}
          disabled={!canTest || isTesting}
        >
          {isTesting ? 'Testing…' : 'Test now'}
        </Button>
      </div>
    </div>
  );
}
