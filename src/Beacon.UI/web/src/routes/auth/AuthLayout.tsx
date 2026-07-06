import {
  useEffect,
  useState,
  forwardRef,
  type ReactNode,
  type InputHTMLAttributes,
  type ButtonHTMLAttributes,
} from 'react';
import { BuildBadge } from '@/components/beacon';

const NODES: Array<[number, number]> = [
  [120, 140], [280, 90], [420, 180], [500, 320],
  [380, 420], [180, 280], [80, 420], [300, 560],
];

export interface AuthLayoutProps {
  /** Eyebrow shown above the form title (e.g. "SIGN IN", "FIRST RUN"). */
  eyebrow: string;
  /** Top-right area on the form panel (e.g. "Don't have an account? Request access"). */
  topbarRight?: ReactNode;
  /** Main title — either plain text or a node with an emphasised `<EmphasisWord />` span. */
  title: ReactNode;
  /** Optional subtitle shown beneath the title. */
  subtitle?: ReactNode;
  /** Form / status content. */
  children: ReactNode;
  /** Hero copy on the left brand panel. Defaults to the Beacon marketing line. */
  leadTitle?: ReactNode;
  leadSub?: ReactNode;
}

/**
 * Shared split-pane chrome for /login, /logout, /setup.
 *
 * Left = atmospheric brand panel (SVG grid + rings + beam + live clock + status rail).
 * Right = `topbarRight`, form title/subtitle, then `children`.
 */
export function AuthLayout({
  eyebrow,
  topbarRight,
  title,
  subtitle,
  children,
  leadTitle,
  leadSub,
}: AuthLayoutProps) {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(t);
  }, []);
  const ts = now.toISOString().slice(11, 19);
  const date = now
    .toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' })
    .toUpperCase();

  return (
    <div className="grid min-h-screen grid-cols-1 bg-bg md:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]">
      <aside className="relative isolate flex min-h-[280px] flex-col justify-between overflow-hidden border-b border-border bg-gradient-to-b from-surface to-surface-2 px-8 py-7 text-text md:min-h-0 md:border-b-0 md:border-r md:px-11 md:pb-8 md:pt-9">
        <div className="pointer-events-none absolute inset-0 z-0 text-text-subtle" aria-hidden>
          <svg viewBox="0 0 600 800" preserveAspectRatio="xMidYMid slice" width="100%" height="100%">
            <defs>
              <pattern id="lp-grid" width="28" height="28" patternUnits="userSpaceOnUse">
                <path d="M 28 0 L 0 0 0 28" fill="none" stroke="currentColor" strokeWidth="0.5" />
              </pattern>
              <radialGradient id="lp-radial" cx="0.1" cy="0.45" r="0.9">
                <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.22" />
                <stop offset="55%" stopColor="var(--brand-500)" stopOpacity="0.05" />
                <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
              </radialGradient>
            </defs>
            <rect
              width="600"
              height="800"
              fill="url(#lp-grid)"
              className="text-text-subtle opacity-[0.16] dark:opacity-[0.10]"
            />
            <rect width="600" height="800" fill="url(#lp-radial)" />

            <g transform="translate(60 520)">
              <circle r="80" fill="none" stroke="var(--brand-500)" strokeOpacity="0.30" className="origin-center animate-beacon-rings" style={{ animationDelay: '0s' }} />
              <circle r="160" fill="none" stroke="var(--brand-500)" strokeOpacity="0.22" className="origin-center animate-beacon-rings" style={{ animationDelay: '1.5s' }} />
              <circle r="260" fill="none" stroke="var(--brand-500)" strokeOpacity="0.15" className="origin-center animate-beacon-rings" style={{ animationDelay: '3s' }} />
              <circle r="380" fill="none" stroke="var(--brand-500)" strokeOpacity="0.08" className="origin-center animate-beacon-rings" style={{ animationDelay: '4.5s' }} />
            </g>

            <g>
              {NODES.map(([x, y], i) => (
                <g key={i} className="animate-beacon-pulse" style={{ animationDelay: `${i * 0.4}s` }}>
                  <circle cx={x} cy={y} r="2.4" fill="var(--brand-400)" opacity="0.7" />
                  <circle cx={x} cy={y} r="9" fill="none" stroke="var(--brand-400)" strokeOpacity="0.25" />
                </g>
              ))}
            </g>

            <g stroke="var(--brand-500)" strokeOpacity="0.18" strokeWidth="0.8" fill="none">
              <path d="M 120 140 L 280 90 L 420 180 L 500 320" />
              <path d="M 180 280 L 380 420 L 300 560" />
              <path d="M 80 420 L 180 280 L 120 140" />
              <path d="M 380 420 L 500 320" />
            </g>
          </svg>
          <span className="beacon-beam" />
        </div>

        <div className="relative z-10 flex flex-1 flex-col gap-8">
          <div className="flex items-center gap-3">
            <img src="/beacon-mark.svg" alt="" aria-hidden className="h-7 w-7" />
            <span className="text-lg font-semibold tracking-tightish">Beacon</span>
            <span className="ml-1 rounded-full border border-border px-2 py-0.5 mono text-2xs text-text-subtle">
              v2.4
            </span>
          </div>

          <div className="my-auto max-w-[460px]">
            <div className="eyebrow mb-3.5 flex-wrap">
              <span className="mono normal-case tracking-normal">{date}</span>
              <span className="eyebrow-sep">·</span>
              <span className="mono normal-case tracking-normal">{ts} UTC</span>
              <span className="eyebrow-sep">·</span>
              <span className="inline-flex items-center gap-1.5">
                <span className="size-1.5 rounded-full bg-ok shadow-[0_0_0_3px_oklch(62%_0.13_155_/_0.18)] animate-beacon-pulse" />
                BEACON ACTIVE
              </span>
            </div>
            <h2 className="m-0 text-[38px] font-semibold leading-[1.1] tracking-tighter text-text max-md:text-[28px]">
              {leadTitle ?? (
                <>
                  Watching the things{' '}
                  <EmphasisWord>that matter</EmphasisWord>.
                </>
              )}
            </h2>
            <p className="mt-3.5 max-w-[420px] text-[14.5px] leading-[1.55] text-text-muted">
              {leadSub ?? (
                <>
                  Query-driven monitoring for data, services and people. Sign in to your workspace to
                  keep an eye on what's running.
                </>
              )}
            </p>
          </div>

          <div className="flex items-center gap-2 text-xs text-text-muted">
            <span>© {now.getFullYear()} Beacon</span>
            <BuildBadge />
          </div>
        </div>
      </aside>

      <main className="flex flex-col px-7 pb-8 pt-6 md:px-14">
        <div className="flex items-center justify-between pb-3">
          <div className="eyebrow">
            <span className="eyebrow-pin" />
            {eyebrow}
          </div>
          {topbarRight && (
            <div className="flex items-center gap-2.5 text-[13px] text-text-muted">{topbarRight}</div>
          )}
        </div>

        <div className="mx-auto my-auto w-full max-w-[380px] py-5">
          <header className="mb-6">
            <h1 className="m-0 text-3xl font-semibold leading-[1.15] tracking-tighter">{title}</h1>
            {subtitle && <p className="mt-2 text-sm text-text-muted">{subtitle}</p>}
          </header>

          {children}
        </div>
      </main>
    </div>
  );
}

/** Italic, brand-coloured word with the hand-drawn underline used in the design. */
export function EmphasisWord({ children }: { children: ReactNode }) {
  return (
    <span className="relative inline-block italic font-semibold text-brand-700 dark:text-brand-300">
      {children}
      <svg className="beacon-underline" viewBox="0 0 220 14" preserveAspectRatio="none" aria-hidden>
        <path
          d="M2 9 Q 40 2, 80 7 T 160 7 T 218 5"
          fill="none"
          stroke="var(--brand-500)"
          strokeWidth="2.5"
          strokeLinecap="round"
        />
      </svg>
    </span>
  );
}

/** Uppercase field label for auth forms. */
export function AuthLabel({ children }: { children: ReactNode }) {
  return (
    <span className="text-xs font-semibold uppercase tracking-eyebrow text-text-muted">
      {children}
    </span>
  );
}

/**
 * Icon-prefixed text field with an optional trailing "Show / Hide" reveal
 * toggle — icon well, focus ring, and reveal button built from Tailwind +
 * Beacon tokens.
 */
export interface AuthFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  icon: ReactNode;
  reveal?: { shown: boolean; onToggle: () => void };
}

export const AuthField = forwardRef<HTMLInputElement, AuthFieldProps>(
  ({ icon, reveal, className, ...props }, ref) => (
    <div className="relative flex items-center rounded-sm border border-border-strong bg-surface transition-colors focus-within:border-brand-500 focus-within:shadow-ring">
      <span className="pointer-events-none absolute left-[11px] text-text-subtle [&>svg]:block">
        {icon}
      </span>
      <input
        ref={ref}
        className={`min-w-0 flex-1 border-0 bg-transparent py-2.5 pl-[34px] pr-3 text-sm text-text outline-none placeholder:text-text-subtle ${className ?? ''}`}
        {...props}
      />
      {reveal && (
        <button
          type="button"
          onClick={reveal.onToggle}
          aria-label={reveal.shown ? 'Hide password' : 'Show password'}
          className="cursor-pointer bg-transparent px-3 text-xs font-semibold uppercase tracking-wide text-text-muted hover:text-text"
        >
          {reveal.shown ? 'Hide' : 'Show'}
        </button>
      )}
    </div>
  ),
);
AuthField.displayName = 'AuthField';

/** Field-level validation message. */
export function AuthFieldError({ children }: { children: ReactNode }) {
  return <span className="text-sm text-crit">{children}</span>;
}

/** Small spinner used inside the submit button / logout rail. */
export function AuthSpinner({ brand = false }: { brand?: boolean }) {
  return (
    <span
      className={
        brand
          ? 'size-3 animate-spin rounded-full border-[1.6px] border-brand-500/40 border-t-brand-500'
          : 'size-3 animate-spin rounded-full border-[1.6px] border-white/35 border-t-white'
      }
    />
  );
}

/**
 * Primary full-width submit / action button for auth forms. Rendered as a
 * `<button>`; for router navigation wrap a `<Link>` with `authLinkButtonClass()`
 * at the call site instead.
 */
export function AuthSubmit({
  children,
  className,
  ...props
}: ButtonHTMLAttributes<HTMLButtonElement>) {
  return (
    <button
      type="submit"
      className={`mt-1 inline-flex items-center justify-center gap-2.5 rounded-sm border border-brand-700 bg-brand-600 px-3.5 py-2.5 text-sm font-semibold text-white transition-colors hover:bg-brand-700 disabled:cursor-not-allowed disabled:opacity-70 ${className ?? ''}`}
      {...props}
    >
      {children}
    </button>
  );
}

/**
 * Same visual treatment as AuthSubmit, but as a class string for use on a
 * router `<Link>`. Content-width (inline-flex) so standalone navigation links
 * hug their label rather than filling the row.
 */
export function authLinkButtonClass(extra?: string): string {
  return `mt-4 inline-flex items-center justify-center gap-2.5 rounded-sm border border-brand-700 bg-brand-600 px-3.5 py-2.5 text-sm font-semibold text-white no-underline transition-colors hover:bg-brand-700 ${extra ?? ''}`;
}

/** Inline form alert chip. */
export function AuthAlert({
  tone,
  children,
}: {
  tone: 'error' | 'info' | 'ok';
  children: ReactNode;
}) {
  const toneClass =
    tone === 'error'
      ? 'bg-crit-bg text-crit border-crit/40'
      : tone === 'ok'
        ? 'bg-ok-bg text-ok border-ok/40'
        : 'bg-info-bg text-info border-info/40';
  return (
    <div className={`mb-4 rounded-sm border px-3 py-2.5 text-[13px] ${toneClass}`} role="alert">
      {children}
    </div>
  );
}
