import { Link } from 'react-router-dom';
import { RefreshCw, Zap } from 'lucide-react';
import { Button, PageHeader, Pill } from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import { SubscriptionStatus, type SubscriptionDetail } from '../queries';

interface SubscriptionHeroProps {
  subscription: SubscriptionDetail;
  canTest: boolean;
  canArchive: boolean;
  isTesting: boolean;
  isArchiving: boolean;
  onTest: () => void;
  onArchive: () => void;
}

export function SubscriptionHero({
  subscription,
  canTest,
  canArchive,
  isTesting,
  isArchiving,
  onTest,
  onArchive,
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
          <Button
            icon={<RefreshCw />}
            onClick={onArchive}
            disabled={!canArchive || !isActive || isArchiving}
          >
            Archive
          </Button>
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
