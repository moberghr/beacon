import { AlertTriangle, Check, Clock } from 'lucide-react';
import { cn } from '@/lib/cn';

export type CheckTone = 'ok' | 'warn' | 'pending';

const checkToneClasses: Record<CheckTone, string> = {
  ok: 'bg-ok-bg text-ok',
  warn: 'bg-warn-bg text-warn',
  pending: 'bg-surface-2 text-text-muted',
};

export interface InfoRowProps {
  label: string;
  value: string;
  detail?: string;
}

/** Two-column key/value row used in the review step of the new-query flow. */
export function InfoRow({ label, value, detail }: InfoRowProps) {
  return (
    <div className="flex items-baseline gap-2">
      <span className="text-xs text-text-muted min-w-[92px]">{label}</span>
      <span className="mono text-sm text-text font-medium">{value}</span>
      {detail && (
        <span className="mono text-text-subtle text-xs ml-auto truncate">{detail}</span>
      )}
    </div>
  );
}

export interface CheckRowProps {
  tone: CheckTone;
  title: string;
  detail?: string;
}

/** Status row (ok / warn / pending) for the new-query review checklist. */
export function CheckRow({ tone, title, detail }: CheckRowProps) {
  const ic =
    tone === 'ok' ? <Check size={11} /> :
    tone === 'warn' ? <AlertTriangle size={11} /> :
    <Clock size={11} />;
  return (
    <div className="flex items-start gap-2.5">
      <span
        className={cn(
          'inline-flex items-center justify-center size-5 rounded-full shrink-0 mt-0.5',
          checkToneClasses[tone],
        )}
      >
        {ic}
      </span>
      <div className="flex-1 min-w-0">
        <div className="text-sm">{title}</div>
        {detail && <div className="text-xs text-text-muted">{detail}</div>}
      </div>
    </div>
  );
}
