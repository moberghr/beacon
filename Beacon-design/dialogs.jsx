// Beacon dialog system — shares the system DNA from PageHeader
// Two example dialogs: Create Project, Create Dashboard
// + a primitive <Modal> with stepped header, eyebrow, signal background

function Modal({ open, onClose, children, width = 640 }) {
  React.useEffect(() => {
    function onKey(e) { if (e.key === "Escape") onClose && onClose(); }
    if (open) document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="modal-scrim" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose && onClose(); }}>
      <div className="modal" style={{ width }} role="dialog" aria-modal="true">
        {children}
      </div>
    </div>
  );
}

function ModalHeader({ eyebrow, title, emphasis, prefix, suffix, sub, variant = "signal", onClose }) {
  return (
    <div className="modal__head">
      <div className={"modal__bg modal__bg--" + variant} aria-hidden="true">
        <ModalHeroVisual variant={variant} />
      </div>
      <div className="modal__head-inner">
        <div className="modal__head-main">
          {eyebrow && (
            <div className="page-hero__eyebrow">
              <span className="page-hero__pin"></span>
              {eyebrow}
            </div>
          )}
          <h2 className="modal__title">
            {prefix && <span>{prefix} </span>}
            {emphasis ? (
              <span className="page-hero__word">
                {emphasis}
                <svg className="page-hero__underline" viewBox="0 0 220 14" preserveAspectRatio="none" aria-hidden="true">
                  <path d="M2 9 Q 40 2, 80 7 T 160 7 T 218 5" fill="none" stroke="var(--brand-500)" strokeWidth="2.5" strokeLinecap="round" />
                </svg>
              </span>
            ) : title}
            {suffix && <span> {suffix}</span>}
          </h2>
          {sub && <p className="modal__sub">{sub}</p>}
        </div>
        <button className="icon-btn modal__close" onClick={onClose} aria-label="Close">
          <Icon.X size={15} />
        </button>
      </div>
    </div>
  );
}

function ModalHeroVisual({ variant }) {
  if (variant === "nodes") {
    return (
      <svg viewBox="0 0 600 100" preserveAspectRatio="none" width="100%" height="100%">
        <defs>
          <pattern id="md-grid-n" width="20" height="20" patternUnits="userSpaceOnUse">
            <path d="M20 0H0V20" fill="none" stroke="currentColor" strokeWidth="0.5" />
          </pattern>
        </defs>
        <rect width="600" height="100" fill="url(#md-grid-n)" className="page-hero__grid" />
        <g className="page-hero__nodes" stroke="var(--brand-500)" fill="var(--brand-500)">
          {[[480,25],[520,55],[440,65],[560,35],[400,40],[470,82]].map(([x,y],i) => (
            <circle key={i} cx={x} cy={y} r="3" opacity="0.85" />
          ))}
          <path d="M400 40 L 480 25 L 520 55 L 440 65 L 470 82 M 480 25 L 560 35 M 520 55 L 560 35" stroke="var(--brand-500)" strokeOpacity="0.4" fill="none" />
        </g>
      </svg>
    );
  }
  return (
    <svg viewBox="0 0 600 100" preserveAspectRatio="none" width="100%" height="100%">
      <defs>
        <pattern id="md-grid-s" width="20" height="20" patternUnits="userSpaceOnUse">
          <path d="M20 0H0V20" fill="none" stroke="currentColor" strokeWidth="0.5" />
        </pattern>
      </defs>
      <rect width="600" height="100" fill="url(#md-grid-s)" className="page-hero__grid" />
      <g transform="translate(560 50)" className="page-hero__arcs">
        <path d="M -180 0 A 180 180 0 0 0 -160 60" fill="none" stroke="var(--brand-500)" strokeOpacity="0.30" strokeWidth="1" />
        <path d="M -120 0 A 120 120 0 0 0 -106 40" fill="none" stroke="var(--brand-500)" strokeOpacity="0.40" strokeWidth="1" />
        <path d="M -60 0 A 60 60 0 0 0 -54 22" fill="none" stroke="var(--brand-500)" strokeOpacity="0.55" strokeWidth="1" />
        <circle cx="0" cy="0" r="4" fill="var(--brand-500)" />
      </g>
    </svg>
  );
}

// ===== Create Project dialog =====
function CreateProjectDialog({ open, onClose }) {
  const [name, setName] = React.useState("");
  const [desc, setDesc] = React.useState("");
  const [sources, setSources] = React.useState(["events-pg"]);
  const [token, setToken] = React.useState("");
  const [repos, setRepos] = React.useState([
    { name: "moberg/beacon-web", branch: "main", visibility: "private" },
  ]);

  const allSources = [
    { id: "events-pg", name: "events", kind: "PostgreSQL", host: "eu-west-1" },
    { id: "billing-mysql", name: "billing", kind: "MySQL", host: "eu-west-1" },
    { id: "analytics-bq", name: "analytics", kind: "BigQuery", host: "us-c1" },
    { id: "ch-events", name: "events-ch", kind: "ClickHouse", host: "eu-west-1" },
  ];

  const valid = name.trim().length > 0;

  return (
    <Modal open={open} onClose={onClose} width={680}>
      <ModalHeader
        variant="nodes"
        eyebrow={<><span>WORKSPACE</span><span className="beacon-hero__sep">/</span><span>DATA</span><span className="beacon-hero__sep">/</span><span>PROJECTS</span></>}
        prefix="Create a new"
        emphasis="project"
        suffix="."
        sub="Group queries, data sources, and repos together so dashboards and alerts have shared context."
        onClose={onClose}
      />

      <div className="modal__body">
        <div className="q-meta-grid">
          <div className="q-field q-field--full">
            <label className="q-label">Project name <span className="q-label__req">*</span></label>
            <input className="q-input" placeholder="e.g. Revenue & retention — EU"
              value={name} onChange={(e) => setName(e.target.value)} autoFocus />
            <span className="q-help">Shown in lists and notifications.</span>
          </div>
          <div className="q-field q-field--full">
            <label className="q-label">Description</label>
            <textarea className="q-textarea" placeholder="Briefly describe what this project tracks…"
              value={desc} onChange={(e) => setDesc(e.target.value)} />
            <span className="q-help">{desc.length}/240 · supports markdown links.</span>
          </div>
        </div>

        <div className="modal__section">
          <div className="modal__section-head">
            <Icon.Database size={14} className="muted" />
            <span className="modal__section-title">Data sources</span>
            <span className="modal__section-meta mono">{sources.length} linked</span>
            <button className="btn btn--ghost" style={{ marginLeft: "auto" }}>
              <Icon.Plus size={13} className="btn__icon" /> Link source
            </button>
          </div>
          <div className="source-list">
            {allSources.map((s) => {
              const linked = sources.includes(s.id);
              return (
                <label key={s.id} className={"source-row" + (linked ? " source-row--on" : "")}>
                  <input type="checkbox" checked={linked} onChange={() => {
                    setSources((arr) => linked ? arr.filter((x) => x !== s.id) : [...arr, s.id]);
                  }} />
                  <span className={"source-row__dot source-row__dot--" + s.kind.toLowerCase()}></span>
                  <span className="source-row__name mono">{s.name}</span>
                  <span className="source-row__kind">{s.kind}</span>
                  <span className="source-row__host mono subtle">{s.host}</span>
                  {linked && <span className="pill pill--ok"><span className="pill__dot"></span>linked</span>}
                </label>
              );
            })}
          </div>
        </div>

        <div className="modal__section">
          <div className="modal__section-head">
            <Icon.Branch size={14} className="muted" />
            <span className="modal__section-title">Repositories</span>
            <span className="modal__section-meta mono">{repos.length} attached</span>
            <button className="btn btn--ghost" style={{ marginLeft: "auto" }}
              onClick={() => setRepos((r) => [...r, { name: "owner/new-repo", branch: "main", visibility: "private" }])}>
              <Icon.Plus size={13} className="btn__icon" /> Add repo
            </button>
          </div>
          {repos.length === 0 ? (
            <div className="empty-strip">
              <Icon.Branch size={14} className="muted" />
              <span>No repositories attached. Beacon won't link query failures to commits.</span>
            </div>
          ) : (
            <div className="repo-list">
              {repos.map((r, i) => (
                <div key={i} className="repo-row">
                  <Icon.Branch size={13} className="muted" />
                  <span className="mono">{r.name}</span>
                  <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>{r.branch}</span>
                  <span className="pill pill--neutral" style={{ fontSize: 10 }}>{r.visibility}</span>
                  <button className="icon-btn" style={{ marginLeft: "auto" }} onClick={() => setRepos((arr) => arr.filter((_, j) => j !== i))}>
                    <Icon.X size={12} />
                  </button>
                </div>
              ))}
            </div>
          )}
          <div className="q-field" style={{ marginTop: 10 }}>
            <label className="q-label" style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <Icon.Lock size={12} /> GitHub access token
              <span className="pill pill--neutral mono" style={{ fontSize: 9, marginLeft: 4 }}>encrypted at rest</span>
            </label>
            <input className="q-input mono" placeholder="ghp_••••••••••••••••••••"
              value={token} onChange={(e) => setToken(e.target.value)} type="password" />
            <span className="q-help">Required for private repositories. Token never leaves the EU region.</span>
          </div>
        </div>
      </div>

      <div className="modal__foot">
        <span className="save-bar__hint">
          <span className="kbd">Esc</span><span>cancel ·</span>
          <span className="kbd">⌘</span><span className="kbd">↵</span><span>create</span>
        </span>
        <div className="spacer"></div>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className={"btn btn--primary" + (valid ? "" : " is-disabled")} disabled={!valid}>
          <Icon.Check size={14} className="btn__icon" /> Create project
        </button>
      </div>
    </Modal>
  );
}

// ===== Create Dashboard dialog =====
function CreateDashboardDialog({ open, onClose }) {
  const [name, setName] = React.useState("");
  const [desc, setDesc] = React.useState("");
  const [shared, setShared] = React.useState(false);
  const [interval, setInterval] = React.useState("60");

  const valid = name.trim().length > 0;
  const intervals = [
    { v: "0", label: "Off" },
    { v: "30", label: "30s" },
    { v: "60", label: "1 min" },
    { v: "300", label: "5 min" },
    { v: "900", label: "15 min" },
  ];

  return (
    <Modal open={open} onClose={onClose} width={520}>
      <ModalHeader
        variant="signal"
        eyebrow={<><span>DASHBOARDS</span><span className="beacon-hero__sep">/</span><span>NEW</span></>}
        prefix="Create a"
        emphasis="dashboard"
        suffix="."
        sub="Pin queries side-by-side. Auto-refresh keeps it live during incidents."
        onClose={onClose}
      />

      <div className="modal__body">
        <div className="q-meta-grid">
          <div className="q-field q-field--full">
            <label className="q-label">Dashboard name <span className="q-label__req">*</span></label>
            <input className="q-input" placeholder="e.g. Mission Control · EU"
              value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </div>
          <div className="q-field q-field--full">
            <label className="q-label">Description</label>
            <textarea className="q-textarea" placeholder="What is this dashboard for? Who watches it?"
              value={desc} onChange={(e) => setDesc(e.target.value)} />
          </div>
        </div>

        <div className="opt-row">
          <button
            type="button"
            role="switch"
            aria-checked={shared}
            className={"toggle" + (shared ? " toggle--on" : "")}
            onClick={() => setShared((v) => !v)}>
            <span className="toggle__thumb"></span>
          </button>
          <div className="opt-row__main">
            <div className="opt-row__title">Share with all users</div>
            <div className="opt-row__sub">Anyone in the workspace can view. Editing still requires your role.</div>
          </div>
          <span className={"pill " + (shared ? "pill--ok" : "pill--neutral")}>
            <span className="pill__dot"></span>{shared ? "shared" : "private"}
          </span>
        </div>

        <div className="opt-row">
          <Icon.Refresh size={16} className="muted" />
          <div className="opt-row__main">
            <div className="opt-row__title">Auto-refresh interval</div>
            <div className="opt-row__sub">How often Beacon re-runs the pinned queries.</div>
          </div>
          <div className="seg" role="tablist" style={{ flexWrap: "nowrap" }}>
            {intervals.map((i) => (
              <button
                key={i.v}
                className={"seg__btn" + (interval === i.v ? " active" : "")}
                onClick={() => setInterval(i.v)}>
                {i.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="modal__foot">
        <span className="save-bar__hint">
          <span className="kbd">Esc</span><span>cancel ·</span>
          <span className="kbd">⌘</span><span className="kbd">↵</span><span>create</span>
        </span>
        <div className="spacer"></div>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className={"btn btn--primary" + (valid ? "" : " is-disabled")} disabled={!valid}>
          <Icon.Check size={14} className="btn__icon" /> Create dashboard
        </button>
      </div>
    </Modal>
  );
}

window.Modal = Modal;
window.ModalHeader = ModalHeader;
window.CreateProjectDialog = CreateProjectDialog;
window.CreateDashboardDialog = CreateDashboardDialog;
