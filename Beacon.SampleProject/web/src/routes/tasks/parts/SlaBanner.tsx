import { useEffect, useRef, useState } from 'react';
import { Icon } from '@/components/Icon';
import { useIsAdmin } from '@/auth/useAuth';
import { useSetSubscriptionSla } from '../queries';

interface SlaBannerProps {
  resolved: boolean;
  ageHours: number;
  slaHours: number;
  resolvedByName: string | null;
  resolvedRelative: string | null;
  notificationCount: number;
  taskId: number;
  subscriptionId: number;
  customSla: boolean;
}

/**
 * High-signal status banner. Uses subscription's SlaHours when set; otherwise
 * the system default (24h). Admins can edit per-subscription SLA inline.
 */
export function SlaBanner({
  resolved,
  ageHours,
  slaHours,
  resolvedByName,
  resolvedRelative,
  notificationCount,
  taskId,
  subscriptionId,
  customSla,
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
        <div className="banner__actions" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span className="sla-meter" title={`${pct}% of SLA elapsed`}>
            <span className="sla-meter__fill" style={{ width: `${pct}%` }} />
          </span>
          <span className="mono subtle" style={{ fontSize: 11 }}>
            {ageHours.toFixed(0)}h / {slaHours}h{customSla ? '' : ' (default)'}
          </span>
          <SlaEditor taskId={taskId} subscriptionId={subscriptionId} currentValue={customSla ? slaHours : null} />
        </div>
      )}
    </div>
  );
}

function SlaEditor({
  taskId,
  subscriptionId,
  currentValue,
}: {
  taskId: number;
  subscriptionId: number;
  currentValue: number | null;
}) {
  const isAdmin = useIsAdmin();
  const [open, setOpen] = useState(false);
  const [value, setValue] = useState<string>(currentValue != null ? String(currentValue) : '');
  const wrap = useRef<HTMLDivElement>(null);
  const setSla = useSetSubscriptionSla(subscriptionId, taskId);

  useEffect(() => {
    setValue(currentValue != null ? String(currentValue) : '');
  }, [currentValue]);

  useEffect(() => {
    if (!open) return;
    const onClickOut = (e: MouseEvent) => {
      if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener('mousedown', onClickOut);
    return () => window.removeEventListener('mousedown', onClickOut);
  }, [open]);

  if (!isAdmin) return null;

  const submit = (clear: boolean) => {
    if (clear) {
      setSla.mutate({ slaHours: null });
      setOpen(false);
      return;
    }
    const n = Number(value);
    if (!Number.isFinite(n) || n < 1 || n > 720) return;
    setSla.mutate({ slaHours: Math.round(n) });
    setOpen(false);
  };

  return (
    <div ref={wrap} style={{ position: 'relative' }}>
      <button
        type="button"
        className="btn btn--ghost"
        title="Edit SLA"
        onClick={() => setOpen(o => !o)}
        style={{ padding: '4px 6px' }}
      >
        <Icon.Cog size={13} />
      </button>
      {open && (
        <div
          style={{
            position: 'absolute',
            right: 0,
            top: 'calc(100% + 4px)',
            background: 'var(--surface)',
            border: '1px solid var(--border)',
            borderRadius: 6,
            boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
            padding: 10,
            zIndex: 30,
            minWidth: 220,
          }}
        >
          <div className="muted" style={{ fontSize: 11.5, marginBottom: 6 }}>
            SLA hours (1–720). Empty = default (24h).
          </div>
          <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
            <input
              type="number"
              min={1}
              max={720}
              className="q-input"
              style={{ flex: 1, fontSize: 12.5 }}
              value={value}
              onChange={e => setValue(e.target.value)}
              disabled={setSla.isPending}
            />
            <button
              type="button"
              className="btn btn--primary"
              disabled={setSla.isPending}
              onClick={() => submit(false)}
            >
              Save
            </button>
          </div>
          <div style={{ marginTop: 6, textAlign: 'right' }}>
            <button
              type="button"
              className="btn btn--ghost"
              disabled={setSla.isPending}
              onClick={() => submit(true)}
            >
              Use default
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
