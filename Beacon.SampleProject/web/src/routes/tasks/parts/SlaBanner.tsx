import { Icon } from '@/components/Icon';

interface SlaBannerProps {
  resolved: boolean;
  ageHours: number;
  slaHours: number;
  resolvedByName: string | null;
  resolvedRelative: string | null;
  notificationCount: number;
}

/**
 * High-signal status banner. SLA percentage is derived from age/slaHours
 * (no backend SLA field — assumed 24h until product confirms otherwise).
 */
export function SlaBanner({
  resolved,
  ageHours,
  slaHours,
  resolvedByName,
  resolvedRelative,
  notificationCount,
}: SlaBannerProps) {
  const pct = Math.min(100, Math.max(0, Math.round((ageHours / slaHours) * 100)));
  const tone = resolved ? 'banner--ok' : pct >= 100 ? 'banner--crit' : pct >= 80 ? 'banner--warn' : 'banner--info';

  return (
    <div className={`banner ${tone}`}>
      <span className="banner__icon">
        {resolved ? <Icon.Check size={16} /> : <Icon.Clock size={16} />}
      </span>
      <div className="banner__main">
        <div className="banner__title">
          {resolved
            ? `Resolved${resolvedByName ? ` by ${resolvedByName}` : ''}${resolvedRelative ? ` · ${resolvedRelative}` : ''}`
            : pct >= 100
              ? 'SLA breached · still awaiting human review'
              : 'Awaiting human review'}
        </div>
        <div className="banner__sub">
          {resolved
            ? 'Subscription will continue to monitor; new alerts open a fresh task.'
            : notificationCount === 0
              ? <>No notifications have been delivered yet — check recipients.</>
              : <>{notificationCount} notification{notificationCount === 1 ? '' : 's'} sent for this alert.</>}
        </div>
      </div>
      {!resolved && (
        <div className="banner__actions">
          <span className="sla-meter" title={`${pct}% of SLA elapsed`}>
            <span className="sla-meter__fill" style={{ width: `${pct}%` }} />
          </span>
          <span className="mono subtle" style={{ fontSize: 11 }}>
            {ageHours.toFixed(0)}h / {slaHours}h
          </span>
        </div>
      )}
    </div>
  );
}
