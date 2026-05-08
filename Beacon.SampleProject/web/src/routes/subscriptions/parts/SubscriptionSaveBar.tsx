import { Icon } from '@/components/Icon';
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
    <div className="save-bar">
      <span className="save-bar__hint">
        {isActive ? (
          <span className="pill pill--ok"><span className="pill__dot" />ACTIVE</span>
        ) : (
          <span className="pill pill--neutral"><span className="pill__dot" />ARCHIVED</span>
        )}
        <span>
          <span className="mono">#{subscription.id}</span>
          {' · '}{totalExecutions.toLocaleString()} run{totalExecutions === 1 ? '' : 's'}
          {' · '}{subscription.recipients.length} recipient{subscription.recipients.length === 1 ? '' : 's'}
        </span>
      </span>
      <div className="spacer" />
      <button
        type="button"
        className="btn"
        onClick={onArchive}
        disabled={!canArchive || !isActive || isArchiving}
      >
        <Icon.Refresh size={14} className="btn__icon" />
        {isArchiving ? ' Archiving…' : ' Archive'}
      </button>
      <button
        type="button"
        className="btn btn--primary"
        onClick={onTest}
        disabled={!canTest || isTesting}
      >
        <Icon.Bolt size={14} className="btn__icon" />
        {isTesting ? ' Testing…' : ' Test now'}
      </button>
    </div>
  );
}
