import { Link } from 'react-router-dom';
import { Icon } from '@/components/Icon';
import { formatDateTime } from '@/lib/format';
import type { SubscriptionDetail } from '../queries';

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
  const isActive = subscription.status === 'Active';

  return (
    <div className="page-hero">
      <div className="page-hero__inner">
        <div className="page-hero__main">
          <div className="page-hero__eyebrow">
            <Link to="/subscriptions" style={{ color: 'inherit', textDecoration: 'none' }}>
              Subscriptions
            </Link>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#{subscription.id}</span>
            <span className="beacon-hero__sep">·</span>
            {isActive ? (
              <span className="pill pill--ok"><span className="pill__dot" />ACTIVE</span>
            ) : (
              <span className="pill pill--neutral"><span className="pill__dot" />ARCHIVED</span>
            )}
            {subscription.aiActorId != null && (
              <>
                <span className="beacon-hero__sep">·</span>
                <span className="pill pill--info mono" style={{ fontSize: 10 }}>AI</span>
              </>
            )}
          </div>
          <h1 className="page-hero__title">
            Watching <span className="page-hero__word">{subscription.queryName}</span>
          </h1>
          <p className="page-hero__sub">
            {subscription.cronDescription || 'no schedule'}
            {subscription.cronNextAt && (
              <>
                {' · '}next <span className="mono">{formatDateTime(subscription.cronNextAt)}</span>
              </>
            )}
            {' · '}
            <span className="mono">{subscription.recipients.length}</span>
            {' '}recipient{subscription.recipients.length === 1 ? '' : 's'}
          </p>
        </div>
        <div className="page-hero__actions">
          <button
            type="button"
            className="btn"
            onClick={onArchive}
            disabled={!canArchive || !isActive || isArchiving}
          >
            <Icon.Refresh size={14} className="btn__icon" /> Archive
          </button>
          <button
            type="button"
            className="btn btn--primary"
            onClick={onTest}
            disabled={!canTest || isTesting}
          >
            <Icon.Bolt size={14} className="btn__icon" /> {isTesting ? 'Testing…' : 'Test now'}
          </button>
        </div>
      </div>
    </div>
  );
}
