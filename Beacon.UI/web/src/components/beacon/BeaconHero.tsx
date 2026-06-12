import * as React from 'react';
import { cn } from '@/lib/cn';

/**
 * BeaconHero — the dashboard radar-sweep hero.
 *
 * Larger than PageHeader; intended for the home / overview page only. The
 * right rail shows last-24h system status as colored ticks plus a
 * "BEACON ACTIVE" live pip in the eyebrow.
 *
 * All animation lives in src/index.css @layer components + the keyframes in
 * tailwind.config (.beacon-beam, .beacon-rings, .beacon-underline).
 */
export interface BeaconHeroProps {
  user?: string;
  /** "ok" | "warn" | "crit" | "muted" for each of the last N hours (left → right). "muted" = no executions in that bucket. */
  ticks?: Array<'ok' | 'warn' | 'crit' | 'muted'>;
  meta: {
    executions30d: number;
    anomalies: number;
    avgMs: number;
  };
  actions?: React.ReactNode;
  className?: string;
}

const defaultTicks: NonNullable<BeaconHeroProps['ticks']> = Array.from({ length: 24 }, () => 'muted');

/** 1 Hz clock, scoped to leaf components so the hero doesn't re-render every second. */
function useNow(): Date {
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(t);
  }, []);
  return now;
}

function HeroEyebrowClock() {
  const now = useNow();
  const dateStr = now
    .toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })
    .toUpperCase();
  const ts = now.toISOString().slice(11, 19);
  return (
    <>
      <span className="mono normal-case tracking-normal">{dateStr}</span>
      <span className="eyebrow-sep">·</span>
      <span className="mono normal-case tracking-normal">{ts} UTC</span>
    </>
  );
}

function HeroGreeting({ user }: { user: string }) {
  const now = useNow();
  const greet =
    now.getHours() < 5
      ? 'Up late'
      : now.getHours() < 12
        ? 'Good morning'
        : now.getHours() < 18
          ? 'Good afternoon'
          : 'Good evening';
  return (
    <>
      {greet}, {user}.
    </>
  );
}

function HeroSeconds() {
  const now = useNow();
  return <>{now.getSeconds().toString().padStart(2, '0')}s</>;
}

export function BeaconHero({
  user = 'there',
  ticks = defaultTicks,
  meta,
  actions,
  className,
}: BeaconHeroProps) {
  const gridId = React.useId();
  const radialId = React.useId();
  const attention = ticks.includes('crit') || meta.anomalies > 0;

  return (
    <section
      className={cn(
        'relative isolate overflow-hidden rounded-lg border border-border',
        'bg-gradient-to-b from-surface to-surface-2',
        className,
      )}
    >
      <div className="absolute inset-0 pointer-events-none z-0 text-border-strong" aria-hidden>
        <svg viewBox="0 0 600 200" preserveAspectRatio="none" width="100%" height="100%">
          <defs>
            <pattern id={gridId} width="24" height="24" patternUnits="userSpaceOnUse">
              <path d="M 24 0 L 0 0 0 24" fill="none" stroke="currentColor" strokeWidth="0.5" />
            </pattern>
            <radialGradient id={radialId} cx="0" cy="0.5" r="1">
              <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.08" />
              <stop offset="60%" stopColor="var(--brand-500)" stopOpacity="0" />
            </radialGradient>
          </defs>
          <rect
            width="600"
            height="200"
            fill={`url(#${gridId})`}
            className="opacity-[0.18] text-text-subtle"
          />
          <rect width="600" height="200" fill={`url(#${radialId})`} />
          <g className="beacon-rings">
            <circle
              cx="0"
              cy="100"
              r="60"
              fill="none"
              stroke="var(--brand-500)"
              strokeOpacity="0.18"
            />
            <circle
              cx="0"
              cy="100"
              r="120"
              fill="none"
              stroke="var(--brand-500)"
              strokeOpacity="0.13"
            />
            <circle
              cx="0"
              cy="100"
              r="180"
              fill="none"
              stroke="var(--brand-500)"
              strokeOpacity="0.08"
            />
          </g>
        </svg>
        <span className="beacon-beam" />
      </div>

      <div className="relative z-10 px-7 py-6 grid gap-7 items-center grid-cols-1 lg:grid-cols-[minmax(0,1fr)_auto]">
        <div className="min-w-0">
          <div className="eyebrow mb-2.5 flex-wrap">
            <HeroEyebrowClock />
            <span className="eyebrow-sep">·</span>
            <span className="inline-flex items-center gap-1.5">
              <span className="size-1.5 rounded-full bg-ok animate-beacon-pulse" />
              BEACON ACTIVE
            </span>
          </div>

          <h1 className="m-0 font-semibold tracking-tighter leading-[1.05]">
            <span className="block text-sm font-medium text-text-muted mb-1.5">
              <HeroGreeting user={user} />
            </span>
            <span className="inline-flex flex-wrap items-baseline gap-x-[0.35em] text-[32px] text-text">
              <span>{attention ? 'Signals need' : 'Everything is'}</span>
              <span className="relative inline-block italic font-semibold text-brand-700 dark:text-brand-300">
                {attention ? 'attention' : 'nominal'}
                <svg
                  className="beacon-underline"
                  viewBox="0 0 220 14"
                  preserveAspectRatio="none"
                  aria-hidden
                >
                  <path
                    d="M2 9 Q 40 2, 80 7 T 160 7 T 218 5"
                    fill="none"
                    stroke="var(--brand-500)"
                    strokeWidth="2.5"
                    strokeLinecap="round"
                  />
                </svg>
              </span>
              <span>.</span>
            </span>
          </h1>

          <p className="mt-3.5 mb-0 text-text-muted text-sm">
            <span>{meta.executions30d.toLocaleString()} queries executed in the last 30 days · </span>
            <span className="mono">{meta.anomalies} anomalies </span>
            <span>· avg </span>
            <span className="mono">{meta.avgMs} ms</span>
          </p>
        </div>

        <div className="flex flex-col items-stretch lg:items-end gap-3.5">
          <div
            className="bg-surface border border-border rounded-sm px-3 pt-2.5 pb-2 w-full lg:w-[280px] shadow-sm"
            title="Last 24 hours of system status"
          >
            <div className="flex items-center justify-between text-2xs font-semibold tracking-eyebrow text-text-muted mb-1.5">
              <span>SYSTEM · LAST 24H</span>
              <span className="mono normal-case tracking-normal">
                <HeroSeconds />
              </span>
            </div>
            <div className="flex items-end gap-0.5 h-[22px]">
              {ticks.map((t, i) => (
                <span
                  key={i}
                  className={cn(
                    'flex-1 rounded-[1px] opacity-90 origin-bottom transition-transform hover:scale-y-110',
                    t === 'ok' && 'bg-ok h-[60%]',
                    t === 'warn' && 'bg-warn h-[80%]',
                    t === 'crit' && 'bg-crit h-[100%]',
                    t === 'muted' && 'bg-border h-[40%] opacity-60',
                  )}
                  title={tickTitle(t, i, ticks.length)}
                />
              ))}
            </div>
            <div className="flex justify-between text-[9.5px] mt-1 mono text-text-subtle">
              <span>−24h</span>
              <span>now</span>
            </div>
          </div>
          {actions && <div className="flex items-center gap-2">{actions}</div>}
        </div>
      </div>
    </section>
  );
}

function tickTitle(t: 'ok' | 'warn' | 'crit' | 'muted', index: number, length: number): string {
  const hoursAgo = length - 1 - index;
  const when = hoursAgo === 0 ? 'this hour' : `${hoursAgo}h ago`;
  if (t === 'muted') return `${when}: no executions`;
  if (t === 'crit') return `${when}: failures`;
  if (t === 'warn') return `${when}: warnings`;
  return `${when}: ok`;
}
