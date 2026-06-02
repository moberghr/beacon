import { useEffect, useRef, useState } from 'react';
import { Check, Clock, Settings } from 'lucide-react';
import { Banner, Button, Input } from '@/components/beacon';
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
  const tone = resolved ? 'ok' : pct >= 100 ? 'crit' : pct >= 80 ? 'warn' : 'info';

  const title = resolved
    ? `Resolved${resolvedByName ? ` by ${resolvedByName}` : ''}${resolvedRelative ? ` · ${resolvedRelative}` : ''}`
    : pct >= 100
      ? 'SLA breached · still awaiting human review'
      : 'Awaiting human review';

  const sub = resolved
    ? 'Subscription will continue to monitor; new alerts open a fresh task.'
    : notificationCount === 0
      ? <>No notifications have been delivered yet — check recipients.</>
      : <>{notificationCount} notification{notificationCount === 1 ? '' : 's'} sent for this alert.</>;

  return (
    <Banner
      tone={tone}
      icon={resolved ? <Check /> : <Clock />}
      title={title}
      sub={sub}
      actions={
        !resolved && (
          <div className="flex items-center gap-2">
            <span
              className="inline-block w-24 h-1.5 bg-surface-2 border border-border rounded-full overflow-hidden"
              title={`${pct}% of SLA elapsed`}
            >
              <span
                className={`block h-full rounded-full ${pct >= 100 ? 'bg-crit' : pct >= 80 ? 'bg-warn' : 'bg-info'}`}
                style={{ width: `${pct}%` }}
              />
            </span>
            <span className="mono text-text-subtle text-xs">
              {ageHours.toFixed(0)}h / {slaHours}h{customSla ? '' : ' (default)'}
            </span>
            <SlaEditor taskId={taskId} subscriptionId={subscriptionId} currentValue={customSla ? slaHours : null} />
          </div>
        )
      }
    />
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
    <div ref={wrap} className="relative">
      <Button
        variant="ghost"
        size="sm"
        title="Edit SLA"
        onClick={() => setOpen(o => !o)}
        icon={<Settings />}
      />
      {open && (
        <div className="absolute right-0 top-[calc(100%+4px)] z-30 min-w-[220px] p-2.5 bg-surface border border-border rounded-sm shadow-pop">
          <div className="text-text-muted text-xs mb-1.5">
            SLA hours (1–720). Empty = default (24h).
          </div>
          <div className="flex gap-1.5 items-center">
            <Input
              type="number"
              min={1}
              max={720}
              className="flex-1 text-xs"
              value={value}
              onChange={e => setValue(e.target.value)}
              disabled={setSla.isPending}
            />
            <Button variant="primary" size="sm" disabled={setSla.isPending} onClick={() => submit(false)}>
              Save
            </Button>
          </div>
          <div className="mt-1.5 text-right">
            <Button variant="ghost" size="sm" disabled={setSla.isPending} onClick={() => submit(true)}>
              Use default
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}
