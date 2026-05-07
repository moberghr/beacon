function AddQueryPage() {
  const [stepName, setStepName] = React.useState("Step 1");
  const [queryName, setQueryName] = React.useState("");
  const [desc, setDesc] = React.useState("");
  const [target, setTarget] = React.useState("test (PostgreSQL)");

  return (
    <div className="page" data-screen-label="02 Add New Query">
      <PageHeader
        variant="signal"
        eyebrow={
          <>
            <a href="#" style={{ color: "inherit", textDecoration: "none" }}>Queries</a>
            <span className="beacon-hero__sep">/</span>
            <span>NEW</span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>DRAFT</span>
          </>
        }
        prefix="Compose a"
        emphasis="cross-source"
        suffix="query."
        sub={<>Chain SQL steps across data sources, pipe results between steps using <span className="mono">@result1</span>, and parameterize with <span className="mono">{`{name}`}</span>.</>}
        actions={
          <>
            <button className="btn"><Icon.Chevron size={14} className="btn__icon" style={{ transform: "rotate(180deg)" }} /> Back</button>
            <button className="btn"><Icon.Bolt size={14} className="btn__icon" /> Run</button>
            <button className="btn btn--primary"><Icon.Check size={14} className="btn__icon" /> Save query</button>
          </>
        }
      />

      <div className="q-layout">
        <div className="q-section">
          <div className="card">
            <div className="card__head">
              <Icon.Info size={15} className="muted" />
              <h3 className="card__title">Query details</h3>
              <div className="card__actions">
                <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>UNSAVED</span>
              </div>
            </div>
            <div className="card__body">
              <div className="q-meta-grid">
                <div className="q-field q-field--full">
                  <label className="q-label">Query name <span className="q-label__req">*</span></label>
                  <input className="q-input" placeholder="e.g. EU signups vs revenue — daily" value={queryName} onChange={(e) => setQueryName(e.target.value)} />
                  <span className="q-help">A short, descriptive name. Shown in lists and notifications.</span>
                </div>
                <div className="q-field q-field--full">
                  <label className="q-label">Description <span className="q-label__req">*</span></label>
                  <textarea className="q-textarea" placeholder="What does this query measure, and why? Include thresholds and audience." value={desc} onChange={(e) => setDesc(e.target.value)} />
                  <span className="q-help">{desc.length}/280 · supports markdown links.</span>
                </div>
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Cross-data-source query builder</h3>
              <span className="card__sub">1 step · 2 params</span>
              <div className="card__actions">
                <button className="btn"><Icon.Sliders size={14} className="btn__icon" /> Variables</button>
                <button className="btn btn--primary"><Icon.Plus size={14} className="btn__icon" /> Add step</button>
              </div>
            </div>
            <div className="card__body">
              <StepCard num={1} stepName={stepName} onStepName={setStepName} target={target} onTarget={setTarget} />
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Query flow</h3>
              <span className="card__sub">step graph</span>
              <div className="card__actions">
                <div className="seg">
                  <button className="seg__btn active">Diagram</button>
                  <button className="seg__btn">JSON</button>
                </div>
              </div>
            </div>
            <div className="flow">
              <div className="flow__node flow__node--db">
                <div className="flow__node-title">STEP 1 · {stepName.toUpperCase()}</div>
                <div className="flow__node-sub">test · postgres</div>
              </div>
              <div className="flow__edge"></div>
              <div className="flow__node flow__node--result">
                <div className="flow__node-title">@result1</div>
                <div className="flow__node-sub">intermediate result</div>
              </div>
              <button className="flow__plus"><Icon.Plus size={12} /> Pipe to next step</button>
            </div>
          </div>
        </div>

        <aside className="q-aside">
          <div className="card">
            <div className="card__head">
              <Icon.Info size={15} className="muted" />
              <h3 className="card__title">Query info</h3>
            </div>
            <div className="card__body" style={{ display: "flex", flexDirection: "column", gap: 10 }}>
              <InfoRow label="Steps" value="1" />
              <InfoRow label="Sources used" value="1" detail="postgres-test" />
              <InfoRow label="Parameters" value="2" detail="{since}, {region}" />
              <InfoRow label="Estimated cost" value="—" detail="run to estimate" />
              <InfoRow label="Owner" value="mirko" />
              <InfoRow label="Last edited" value="just now" />
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Check size={15} className="muted" />
              <h3 className="card__title">Pre-flight checks</h3>
              <div className="card__actions">
                <span className="pill pill--warn">2 to fix</span>
              </div>
            </div>
            <div className="checks">
              <Check tone="ok" title="Target source reachable" detail="test · 38ms" time="3s ago" />
              <Check tone="ok" title="SQL parses" detail="postgres dialect" time="3s ago" />
              <Check tone="warn" title="Query name is required" detail="add a descriptive name" />
              <Check tone="warn" title="Description is required" detail="explain what this measures" />
              <Check tone="pending" title="Run to validate output" detail="dry-run not executed yet" />
            </div>
          </div>

          <div className="callout">
            <Icon.Lightbulb size={16} className="callout__icon" />
            <div>
              <div className="callout__title">Tip · pipe results</div>
              <div className="callout__sub">Reference a previous step using <span className="mono">@result1</span> in your <span className="mono">FROM</span> clause to chain across sources.</div>
            </div>
          </div>
        </aside>
      </div>

      <div className="save-bar">
        <span className="save-bar__hint">
          <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>DRAFT</span>
          <span>Auto-saved <span className="mono">3s ago</span> to your workspace.</span>
        </span>
        <div className="spacer"></div>
        <span className="save-bar__hint">
          <span>Press</span>
          <span className="kbd">⌘</span><span className="kbd">↵</span>
          <span>to run ·</span>
          <span className="kbd">⌘</span><span className="kbd">S</span>
          <span>to save</span>
        </span>
        <button className="btn"><Icon.Bolt size={14} className="btn__icon" /> Run</button>
        <button className="btn btn--primary"><Icon.Check size={14} className="btn__icon" /> Save query</button>
      </div>
    </div>
  );
}

function InfoRow({ label, value, detail }) {
  return (
    <div style={{ display: "flex", alignItems: "baseline", gap: 8 }}>
      <span style={{ fontSize: 12, color: "var(--text-muted)", minWidth: 92 }}>{label}</span>
      <span className="mono" style={{ fontSize: 13, color: "var(--text)", fontWeight: 500 }}>{value}</span>
      {detail && <span className="mono subtle" style={{ fontSize: 11.5, marginLeft: "auto" }}>{detail}</span>}
    </div>
  );
}

function Check({ tone, title, detail, time }) {
  const ic = tone === "ok" ? <Icon.Check size={11} /> : tone === "warn" ? <Icon.Alert size={11} /> : <Icon.Clock size={11} />;
  return (
    <div className="check">
      <div className={"check__icon check__icon--" + tone}>{ic}</div>
      <div>
        <div className="check__main">{title}</div>
        <div className="check__detail">{detail}</div>
      </div>
      {time && <div className="check__time">{time}</div>}
    </div>
  );
}

function StepCard({ num, stepName, onStepName, target, onTarget }) {
  return (
    <div className="step-card">
      <div className="step-card__head">
        <span className="step-num">{String(num).padStart(2, "0")}</span>
        <span className="step-name">Step {num}</span>
        <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>not run</span>
        <div style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
          <button className="icon-btn" title="Duplicate"><Icon.Layers size={14} /></button>
          <button className="icon-btn" title="More"><Icon.Dots size={14} /></button>
        </div>
      </div>
      <div className="step-card__body">
        <div className="step-row">
          <div className="q-field">
            <label className="q-label">Step name</label>
            <input className="q-input" value={stepName} onChange={(e) => onStepName(e.target.value)} />
          </div>
          <div className="q-field">
            <label className="q-label">Target database</label>
            <select className="q-select" value={target} onChange={(e) => onTarget(e.target.value)}>
              <option>test (PostgreSQL)</option>
              <option>analytics (BigQuery)</option>
              <option>events (ClickHouse)</option>
              <option>billing (MySQL)</option>
            </select>
            <span className="q-help mono" style={{ display: "inline-flex", alignItems: "center", gap: 6 }}>
              <span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--ok)" }}></span>
              connected · 38ms ping · ro replica
            </span>
          </div>
        </div>

        <div>
          <div className="q-label" style={{ marginBottom: 4 }}>SQL query</div>
          <div className="q-help" style={{ marginBottom: 8 }}>
            First step has no upstream results. Use <span className="mono tok-prm">{`{param_name}`}</span> for parameters and <span className="mono tok-ref">@resultN</span> to reference earlier steps.
          </div>
          <Sql />
        </div>

        <div className="params">
          <div className="params__head">
            <div className="q-label" style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
              Parameters
              <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>2 detected</span>
            </div>
            <button className="btn btn--ghost"><Icon.Refresh size={13} className="btn__icon" /> Re-scan</button>
          </div>
          <div className="params__chips">
            <span className="param-chip"><span>{`{since}`}</span><span className="param-chip__type">date</span></span>
            <span className="param-chip"><span>{`{region}`}</span><span className="param-chip__type">text</span></span>
            <button className="chip"><Icon.Plus size={11} /> add parameter</button>
          </div>
        </div>
      </div>
    </div>
  );
}

function Sql() {
  const Tok = ({ t, v }) => {
    if (t === "kw") return <span className="tok-kw">{v}</span>;
    if (t === "fn") return <span className="tok-fn">{v}</span>;
    if (t === "str") return <span className="tok-str">{v}</span>;
    if (t === "num") return <span className="tok-num">{v}</span>;
    if (t === "com") return <span className="tok-com">{v}</span>;
    if (t === "prm") return <span className="tok-prm">{v}</span>;
    if (t === "ref") return <span className="tok-ref">{v}</span>;
    return <span>{v}</span>;
  };

  const lines = [
    [{ t: "kw", v: "WITH " }, { t: "fn", v: "recent_signups" }, { t: "kw", v: " AS " }, { t: "txt", v: "(" }],
    [{ t: "txt", v: "  " }, { t: "kw", v: "SELECT" }, { t: "txt", v: " region, " }, { t: "fn", v: "date_trunc" }, { t: "txt", v: "(" }, { t: "str", v: "'day'" }, { t: "txt", v: ", created_at) " }, { t: "kw", v: "AS " }, { t: "txt", v: "d, " }, { t: "fn", v: "count" }, { t: "txt", v: "(*) signups" }],
    [{ t: "txt", v: "  " }, { t: "kw", v: "FROM" }, { t: "txt", v: " events.signups" }],
    [{ t: "txt", v: "  " }, { t: "kw", v: "WHERE" }, { t: "txt", v: " created_at " }, { t: "kw", v: ">=" }, { t: "txt", v: " " }, { t: "prm", v: "{since}" }],
    [{ t: "txt", v: "    " }, { t: "kw", v: "AND" }, { t: "txt", v: " region " }, { t: "kw", v: "=" }, { t: "txt", v: " " }, { t: "prm", v: "{region}" }],
    [{ t: "txt", v: "  " }, { t: "kw", v: "GROUP BY " }, { t: "num", v: "1" }, { t: "txt", v: ", " }, { t: "num", v: "2" }],
    [{ t: "txt", v: ")" }],
    [{ t: "kw", v: "SELECT" }, { t: "txt", v: " r.d, r.region, r.signups," }],
    [{ t: "txt", v: "       " }, { t: "fn", v: "round" }, { t: "txt", v: "(b.revenue " }, { t: "kw", v: "/" }, { t: "txt", v: " " }, { t: "fn", v: "nullif" }, { t: "txt", v: "(r.signups, " }, { t: "num", v: "0" }, { t: "txt", v: "), " }, { t: "num", v: "2" }, { t: "txt", v: ") " }, { t: "kw", v: "AS " }, { t: "txt", v: "arpu" }],
    [{ t: "kw", v: "FROM" }, { t: "txt", v: " recent_signups r" }],
    [{ t: "kw", v: "LEFT JOIN " }, { t: "ref", v: "@result_billing" }, { t: "txt", v: " b " }, { t: "kw", v: "USING " }, { t: "txt", v: "(d, region)" }],
    [{ t: "kw", v: "ORDER BY " }, { t: "txt", v: "r.d " }, { t: "kw", v: "DESC" }, { t: "txt", v: "  " }, { t: "com", v: "-- newest first" }],
    [{ t: "kw", v: "LIMIT " }, { t: "num", v: "100" }, { t: "txt", v: ";" }],
  ];

  return (
    <div className="sql">
      <aside className="sql__sidebar">
        <div className="sql__sidebar-head">
          <Icon.Database size={13} />
          Database explorer
          <span className="mono">15 tables</span>
        </div>
        <div className="sql__schemas">
          <div className="sql__schema-row">
            <Icon.ChevronDown size={11} />
            <span style={{ width: 9, height: 9, borderRadius: 2, background: "var(--brand-500)" }}></span>
            <span>beacon</span>
            <span className="subtle">3 tables</span>
          </div>
          <div className="sql__table-row"><Icon.Box size={10} /> users</div>
          <div className="sql__table-row" style={{ background: "var(--brand-50)", color: "var(--brand-700)" }}>
            <Icon.Box size={10} /> signups
            <span style={{ marginLeft: "auto" }} className="mono subtle">in use</span>
          </div>
          <div className="sql__table-row"><Icon.Box size={10} /> sessions</div>
          <div className="sql__schema-row" style={{ marginTop: 4 }}>
            <Icon.ChevronDown size={11} />
            <span style={{ width: 9, height: 9, borderRadius: 2, background: "oklch(60% 0.13 240)" }}></span>
            <span>hangfire</span>
            <span className="subtle">12 tables</span>
          </div>
          <div className="sql__table-row"><Icon.Box size={10} /> jobs</div>
          <div className="sql__table-row"><Icon.Box size={10} /> state</div>
          <div className="sql__table-row"><Icon.Box size={10} /> server</div>
        </div>
      </aside>
      <div className="sql__editor">
        <div className="sql__toolbar">
          <span className="sql__tab"><Icon.Bolt size={11} /> step1.sql</span>
          <span className="subtle" style={{ marginLeft: "auto" }}>postgres · ro</span>
          <span className="subtle">·</span>
          <span>line 12, col 18</span>
        </div>
        <div className="sql__code">
          <div className="sql__gutter">
            {lines.map((_, i) => <span key={i}>{i + 1}</span>)}
          </div>
          <pre className="sql__pre">
            {lines.map((line, i) => (
              <div key={i}>
                {line.map((tk, j) => <Tok key={j} t={tk.t} v={tk.v} />)}
                {i === 11 && <span className="sql__caret">&nbsp;</span>}
              </div>
            ))}
          </pre>
        </div>
        <div className="sql__statusbar">
          <span className="ok">● parsed</span>
          <span>13 lines · 312 chars</span>
          <span style={{ marginLeft: "auto" }}>2 params · 1 @result ref</span>
          <span className="kbd" style={{ fontSize: 10 }}>⌘</span>
          <span className="kbd" style={{ fontSize: 10 }}>↵</span>
          <span>run</span>
        </div>
      </div>
    </div>
  );
}

window.AddQueryPage = AddQueryPage;
