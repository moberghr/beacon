import { Link } from 'react-router-dom';
import { RefreshCw, RotateCcw, Zap } from 'lucide-react';
import { Button, PageHeader, Pill } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import { SubscriptionStatus, type SubscriptionDetail } from '../queries';

interface SubscriptionHeroProps {
  subscription: SubscriptionDetail;
  canTest: boolean;
  canArchive: boolean;
  isTesting: boolean;
  isArchiving: boolean;
  isReactivating: boolean;
  onTest: () => void;
  onArchive: () => void;
  onReactivate: () => void;
}

export function SubscriptionHero({
  subscription,
  canTest,
  canArchive,
  isTesting,
  isArchiving,
  isReactivating,
  onTest,
  onArchive,
  onReactivate,
}: SubscriptionHeroProps) {
  const isActive = subscription.status === SubscriptionStatus.Active;

  return (
    <PageHeader
      variant="signal"
      eyebrow={
        <>
          <Link to="/subscriptions" className="hover:text-text">
            Subscriptions
          </Link>
          <span className="eyebrow-sep">/</span>
          <span className="mono normal-case tracking-normal">#{subscription.id}</span>
          <span className="eyebrow-sep">·</span>
          {isActive ? (
            <Pill tone="ok" dot>ACTIVE</Pill>
          ) : (
            <Pill dot>ARCHIVED</Pill>
          )}
          {subscription.aiActorId != null && (
            <>
              <span className="eyebrow-sep">·</span>
              <Pill tone="info">AI</Pill>
            </>
          )}
        </>
      }
      prefix="Watching"
      emphasis={subscription.queryName}
      sub={
        <>
          {subscription.cronDescription || 'no schedule'}
          {subscription.cronNextAt && (
            <>
              {' · '}next <span className="mono">{formatDateTime(subscription.cronNextAt)}</span>
            </>
          )}
          {' · '}
          <span className="mono">{subscription.recipients.length}</span>
          {' '}recipient{subscription.recipients.length === 1 ? '' : 's'}
        </>
      }
      actions={
        <>
          {isActive ? (
            <Button
              icon={<RefreshCw />}
              onClick={onArchive}
              disabled={!canArchive || isArchiving}
            >
              Archive
            </Button>
          ) : (
            <Button
              icon={<RotateCcw />}
              onClick={onReactivate}
              disabled={!canArchive || isReactivating}
            >
              {isReactivating ? 'Reactivating…' : 'Reactivate'}
            </Button>
          )}
          <Button
            variant="primary"
            icon={<Zap />}
            onClick={onTest}
            disabled={!canTest || isTesting}
          >
            {isTesting ? 'Testing…' : 'Test now'}
          </Button>
        </>
      }
    />
  );
}
