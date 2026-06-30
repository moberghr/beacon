import { useEffect, useState, type ReactNode } from 'react';
import { BuildBadge } from '@/components/beacon';
import './login.css';

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
    <div className="login">
      <aside className="login__aside">
        <div className="login__aside-bg">
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
            <rect width="600" height="800" fill="url(#lp-grid)" className="login__grid" />
            <rect width="600" height="800" fill="url(#lp-radial)" />

            <g className="login__rings" transform="translate(60 520)">
              <circle r="80" fill="none" stroke="var(--brand-500)" strokeOpacity="0.30" />
              <circle r="160" fill="none" stroke="var(--brand-500)" strokeOpacity="0.22" />
              <circle r="260" fill="none" stroke="var(--brand-500)" strokeOpacity="0.15" />
              <circle r="380" fill="none" stroke="var(--brand-500)" strokeOpacity="0.08" />
            </g>

            <g className="login__nodes">
              {NODES.map(([x, y], i) => (
                <g key={i} style={{ animationDelay: `${i * 0.4}s` }}>
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
          <span className="login__beam" />
        </div>

        <div className="login__aside-content">
          <div className="login__brand">
            <img src="/beacon-mark.svg" alt="" aria-hidden className="login__brand-mark" />
            <span className="login__brand-name">Beacon</span>
            <span className="login__brand-ver">v2.4</span>
          </div>

          <div className="login__lead">
            <div className="login__eyebrow">
              <span className="mono">{date}</span>
              <span className="login__eyebrow-sep">·</span>
              <span className="mono">{ts} UTC</span>
              <span className="login__eyebrow-sep">·</span>
              <span className="login__live-pip">
                <span className="login__live-pip-dot" />
                BEACON ACTIVE
              </span>
            </div>
            <h2 className="login__lead-title">
              {leadTitle ?? (
                <>
                  Watching the things{' '}
                  <EmphasisWord>that matter</EmphasisWord>.
                </>
              )}
            </h2>
            <p className="login__lead-sub">
              {leadSub ?? (
                <>
                  Query-driven monitoring for data, services and people. Sign in to your workspace to
                  keep an eye on what's running.
                </>
              )}
            </p>
          </div>

          <div className="login__foot">
            <span>© {now.getFullYear()} Beacon</span>
            <BuildBadge />
          </div>
        </div>
      </aside>

      <main className="login__main">
        <div className="login__topbar">
          <div className="eyebrow">
            <span className="eyebrow-pin" />
            {eyebrow}
          </div>
          {topbarRight && <div className="login__topbar-right">{topbarRight}</div>}
        </div>

        <div className="login__form-wrap">
          <header className="login__form-head">
            <h1 className="login__title">{title}</h1>
            {subtitle && <p className="login__sub">{subtitle}</p>}
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
    <span className="login__lead-word">
      {children}
      <svg className="login__underline" viewBox="0 0 220 14" preserveAspectRatio="none" aria-hidden>
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

/** Inline form alert chip. */
export function AuthAlert({
  tone,
  children,
}: {
  tone: 'error' | 'info' | 'ok';
  children: ReactNode;
}) {
  return (
    <div className={`login__alert login__alert--${tone}`} role="alert">
      {children}
    </div>
  );
}
