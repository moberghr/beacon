import * as React from 'react';
import { cn } from '@/lib/cn';

/**
 * Compact page hero in the Beacon system.
 *
 * Three variants share the same skeleton (grid + emphasis word + underline)
 * but swap the background motif:
 *   - signal: scanning arcs + grid (default — pages about queries/alerts)
 *   - nodes:  pulsing graph nodes  (pages about projects / sources)
 *   - pulse:  ECG-like waveform    (pages about activity / health)
 */
export interface PageHeaderProps {
  variant?: 'signal' | 'nodes' | 'pulse';
  eyebrow?: React.ReactNode;
  prefix?: React.ReactNode;
  emphasis: React.ReactNode;
  suffix?: React.ReactNode;
  sub?: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
}

export function PageHeader({
  variant = 'signal',
  eyebrow,
  prefix,
  emphasis,
  suffix,
  sub,
  actions,
  className,
}: PageHeaderProps) {
  return (
    <header
      className={cn(
        'relative isolate overflow-hidden rounded-lg border border-border',
        'bg-gradient-to-b from-surface to-surface-2',
        className,
      )}
    >
      <svg
        className="absolute inset-0 w-full h-full text-text-subtle opacity-[0.14] pointer-events-none"
        aria-hidden
      >
        <defs>
          <pattern id="page-grid" width="28" height="28" patternUnits="userSpaceOnUse">
            <path d="M 28 0 L 0 0 0 28" fill="none" stroke="currentColor" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="100%" height="100%" fill="url(#page-grid)" />
      </svg>

      {variant === 'signal' && <span className="beacon-beam" aria-hidden />}
      {variant === 'nodes' && <NodesMotif />}
      {variant === 'pulse' && <PulseMotif />}

      <div className="relative px-6 py-5 flex items-start gap-4">
        <div className="flex-1 min-w-0">
          {eyebrow && (
            <div className="eyebrow mb-2">
              <span className="eyebrow-pin" />
              {eyebrow}
            </div>
          )}
          <h1 className="m-0 text-2xl font-semibold leading-tight tracking-tighter">
            {prefix && <span className="text-text">{prefix} </span>}
            <span className="relative inline-block italic font-semibold text-brand-700 dark:text-brand-300">
              {emphasis}
              <svg
                className="beacon-underline"
                viewBox="0 0 240 12"
                preserveAspectRatio="none"
                aria-hidden
              >
                <path
                  d="M2 8 C 40 2, 80 11, 120 6 S 200 3, 238 7"
                  fill="none"
                  stroke="var(--brand-500)"
                  strokeWidth="2"
                  strokeLinecap="round"
                />
              </svg>
            </span>
            {suffix && <span className="text-text"> {suffix}</span>}
          </h1>
          {sub && <p className="m-0 mt-2 text-sm text-text-muted">{sub}</p>}
        </div>
        {actions && <div className="shrink-0 flex items-center gap-2">{actions}</div>}
      </div>
    </header>
  );
}

function NodesMotif() {
  return (
    <svg
      className="absolute right-0 top-0 h-full w-[40%] opacity-50 pointer-events-none"
      viewBox="0 0 320 140"
      aria-hidden
    >
      {([[60, 40], [140, 60], [220, 30], [260, 90], [100, 100]] as const).map(([x, y], i) => (
        <g key={i}>
          <circle cx={x} cy={y} r="3" fill="var(--brand-500)" opacity="0.7" />
          <circle
            cx={x}
            cy={y}
            r="8"
            fill="none"
            stroke="var(--brand-500)"
            strokeOpacity="0.25"
            style={{ animation: `beacon-rings 4s ${i * 0.4}s ease-out infinite` }}
          />
        </g>
      ))}
    </svg>
  );
}

function PulseMotif() {
  return (
    <svg
      className="absolute right-0 top-0 h-full w-[45%] opacity-45 pointer-events-none"
      viewBox="0 0 400 100"
      preserveAspectRatio="none"
      aria-hidden
    >
      <path
        d="M0 50 L 80 50 L 100 30 L 120 70 L 140 20 L 160 80 L 180 50 L 400 50"
        fill="none"
        stroke="var(--brand-500)"
        strokeWidth="1.5"
      />
    </svg>
  );
}
