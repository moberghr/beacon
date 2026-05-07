// Compact, page-level header that shares Beacon DNA but is shorter
// Variants change the right-side "instrument" visual to fit the page context

function PageHeader({
  eyebrow,        // small uppercase eyebrow, e.g. "Queries / new"
  title,          // main title, can include <em> for emphasis
  emphasis,       // string -> rendered as italicized brand-color word inside title
  prefix = "",    // text before emphasis
  suffix = "",    // text after emphasis
  sub,            // small description line
  meta = [],      // pills/chips on the right of the eyebrow
  variant = "grid",  // grid | signal | nodes | radar
  actions = null,
}) {
  return (
    <div className="page-hero">
      <div className={"page-hero__bg page-hero__bg--" + variant} aria-hidden="true">
        <PageHeroVisual variant={variant} />
      </div>

      <div className="page-hero__inner">
        <div className="page-hero__main">
          {eyebrow && (
            <div className="page-hero__eyebrow">
              <span className="page-hero__pin" aria-hidden="true"></span>
              {eyebrow}
              {meta.length > 0 && <span className="beacon-hero__sep">·</span>}
              {meta.map((m, i) => <React.Fragment key={i}>{m}</React.Fragment>)}
            </div>
          )}
          <h1 className="page-hero__title">
            {prefix && <span>{prefix} </span>}
            {emphasis && (
              <span className="page-hero__word">
                {emphasis}
                <svg className="page-hero__underline" viewBox="0 0 220 14" preserveAspectRatio="none" aria-hidden="true">
                  <path d="M2 9 Q 40 2, 80 7 T 160 7 T 218 5" fill="none" stroke="var(--brand-500)" strokeWidth="2.5" strokeLinecap="round" />
                </svg>
              </span>
            )}
            {suffix && <span> {suffix}</span>}
            {!emphasis && !prefix && !suffix && title}
          </h1>
          {sub && <p className="page-hero__sub">{sub}</p>}
        </div>
        {actions && <div className="page-hero__actions">{actions}</div>}
      </div>
    </div>
  );
}

function PageHeroVisual({ variant }) {
  if (variant === "signal") {
    // Concentric arcs + scanning column — for query/build pages
    return (
      <svg viewBox="0 0 600 120" preserveAspectRatio="none" width="100%" height="100%">
        <defs>
          <linearGradient id="ph-signal-fade" x1="0" y1="0" x2="1" y2="0">
            <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0" />
            <stop offset="60%" stopColor="var(--brand-500)" stopOpacity="0.18" />
            <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
          </linearGradient>
          <pattern id="ph-grid" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M20 0H0V20" fill="none" stroke="currentColor" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="600" height="120" fill="url(#ph-grid)" className="page-hero__grid" />
        <g transform="translate(560 60)" className="page-hero__arcs">
          <path d="M -180 0 A 180 180 0 0 0 -160 60" fill="none" stroke="var(--brand-500)" strokeOpacity="0.30" strokeWidth="1" />
          <path d="M -120 0 A 120 120 0 0 0 -106 40" fill="none" stroke="var(--brand-500)" strokeOpacity="0.40" strokeWidth="1" />
          <path d="M -60 0 A 60 60 0 0 0 -54 22" fill="none" stroke="var(--brand-500)" strokeOpacity="0.55" strokeWidth="1" />
          <circle cx="0" cy="0" r="4" fill="var(--brand-500)" />
        </g>
        <rect x="0" y="0" width="120" height="120" fill="url(#ph-signal-fade)" className="page-hero__scan" />
      </svg>
    );
  }
  if (variant === "nodes") {
    // Tiny graph of connected nodes — for catalog / sources / migration
    return (
      <svg viewBox="0 0 600 120" preserveAspectRatio="none" width="100%" height="100%">
        <defs>
          <pattern id="ph-grid2" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M20 0H0V20" fill="none" stroke="currentColor" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="600" height="120" fill="url(#ph-grid2)" className="page-hero__grid" />
        <g className="page-hero__nodes" stroke="var(--brand-500)" fill="var(--brand-500)">
          {[[480,30],[520,60],[440,70],[560,40],[400,40],[470,95]].map(([x,y],i)=>(
            <circle key={i} cx={x} cy={y} r="3" opacity="0.8" />
          ))}
          <path d="M400 40 L 480 30 L 520 60 L 440 70 L 470 95 M 480 30 L 560 40 M 520 60 L 560 40" stroke="var(--brand-500)" strokeOpacity="0.4" fill="none" />
        </g>
      </svg>
    );
  }
  // default: grid + sweep beam (like dashboard but compact)
  return (
    <svg viewBox="0 0 600 120" preserveAspectRatio="none" width="100%" height="100%">
      <defs>
        <pattern id="ph-grid3" width="20" height="20" patternUnits="userSpaceOnUse">
          <path d="M20 0H0V20" fill="none" stroke="currentColor" strokeWidth="0.5" />
        </pattern>
        <radialGradient id="ph-rad" cx="0%" cy="50%" r="50%">
          <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.30" />
          <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
        </radialGradient>
      </defs>
      <rect width="600" height="120" fill="url(#ph-grid3)" className="page-hero__grid" />
      <rect width="600" height="120" fill="url(#ph-rad)" />
    </svg>
  );
}

window.PageHeader = PageHeader;
