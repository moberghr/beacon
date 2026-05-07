function QueryDetailPage() {
  return (
    <div className="page" data-screen-label="03 Query Detail">
      <PageHeader
        variant="signal"
        eyebrow={
          <>
            <a href="#" style={{ color: "inherit", textDecoration: "none" }}>Queries</a>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#1</span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--ok"><span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--ok)" }}></span>ACTIVE</span>
            <span className="beacon-hero__sep">·</span>
            <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>USER-DEFINED</span>
          </>
        }
        prefix="Editing"
        emphasis="test"
        suffix=""
        sub={<>Single-step query against <span className="mono">postgres-test</span> · created <span className="mono">04 May 2026 · 15:26</span> · 0 runs to date</>}
        actions={
          <>
            <button className="btn"><Icon.Download size={14} className="btn__icon" /> Export</button>
            <button className="btn"><Icon.Plus size={14} className="btn__icon" /> Add subscription</button>
            <button className="btn btn--primary"><Icon.Bolt size={14} className="btn__icon" /> Execute query</button>
          </>
        }
      />

      {/* Activity / metrics row — replaces the colored tiles */}
      <div className="kpi-grid">
        <KPI dot="info" label="Executions" value="0" sub={<><span className="pill pill--neutral">never run</span></>} />
        <KPI dot="brand" label="Results rate" value="N/A" sub="run to populate" />
        <KPI dot="ok" label="Subscriptions" value="0" sub="not yet subscribed" />
        <KPI dot="warn" label="Query steps" value="1" sub="single-step" />
      </div>
      <div className="row row--3-up">
        <PerfKPI dot="ok" label="Avg execution" value="—" unit="ms" sub="no samples yet" />
        <PerfKPI dot="ok" label="Fastest" value="—" unit="ms" sub="no samples yet" />
        <PerfKPI dot="warn" label="Slowest" value="—" unit="ms" sub="no samples yet" />
      </div>

      <div className="q-layout">
        <div className="q-section">
          {/* Query info — restructured as a clean key/value table, not 4 colored bars */}
          <div className="card">
            <div className="card__head">
              <Icon.Info size={15} className="muted" />
              <h3 className="card__title">Query information</h3>
              <div className="card__actions">
                <button className="btn btn--ghost"><Icon.Cog size={13} className="btn__icon" /> Settings</button>
              </div>
            </div>
            <div className="card__body">
              <div className="kv">
                <KV label="Query ID" value={<span className="mono">#1</span>} />
                <KV label="Query type" value={<span className="pill pill--neutral mono">single-step</span>} />
                <KV label="Created" value={<span className="mono">04 May 2026 · 15:26 UTC</span>} />
                <KV label="Last edited" value={<span className="mono">just now · by mirko</span>} />
                <KV label="Data source" value={<span className="mono" style={{ display: "inline-flex", alignItems: "center", gap: 6 }}><span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--ok)" }}></span>test</span>} />
                <KV label="Database engine" value={<span className="mono">PostgreSQL · 38ms ping</span>} />
                <KV label="Owner" value={<span className="mono">mirko · admin</span>} />
                <KV label="Visibility" value={<span className="pill pill--neutral mono">workspace</span>} />
              </div>
            </div>
          </div>

          {/* Query steps */}
          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Query steps</h3>
              <span className="card__sub">1 step · 0 params</span>
              <div className="card__actions">
                <button className="btn"><Icon.Plus size={13} className="btn__icon" /> Add step</button>
                <button className="btn btn--primary"><Icon.Bolt size={13} className="btn__icon" /> Execute query</button>
              </div>
            </div>
            <div className="card__body">
              <div className="step-card">
                <div className="step-card__head">
                  <span className="step-num">01</span>
                  <span className="step-name">Step 1</span>
                  <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>not run</span>
                  <span className="mono subtle" style={{ fontSize: 11.5, marginLeft: 8 }}>test · postgres</span>
                  <div style={{ marginLeft: "auto", display: "flex", gap: 4 }}>
                    <button className="icon-btn" title="Run step"><Icon.Bolt size={14} /></button>
                    <button className="icon-btn" title="Edit"><Icon.Sliders size={14} /></button>
                    <button className="icon-btn" title="More"><Icon.Dots size={14} /></button>
                  </div>
                </div>
                <div className="step-card__body">
                  <div>
                    <div className="q-label" style={{ marginBottom: 8, display: "flex", alignItems: "center", gap: 8 }}>
                      SQL query
                      <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>read-only · click edit</span>
                    </div>
                    <SimpleSql />
                  </div>
                  <div className="params">
                    <div className="params__head">
                      <div className="q-label" style={{ display: "inline-flex", alignItems: "center", gap: 8 }}>
                        Parameters
                        <span className="pill pill--neutral mono" style={{ fontSize: 10 }}>none detected</span>
                      </div>
                      <button className="btn btn--ghost"><Icon.Refresh size={13} className="btn__icon" /> Re-scan</button>
                    </div>
                    <div className="params__chips">
                      <button className="chip"><Icon.Plus size={11} /> add parameter</button>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Flow diagram — cleaner version of the existing one */}
          <div className="card">
            <div className="card__head">
              <Icon.Branch size={15} className="muted" />
              <h3 className="card__title">Query flow diagram</h3>
              <div className="card__actions">
                <div className="seg">
                  <button className="seg__btn active">Diagram</button>
                  <button className="seg__btn">JSON</button>
                </div>
              </div>
            </div>
            <div className="flow">
              <div className="flow__node flow__node--result" style={{ background: "var(--warn-bg)", color: "var(--warn)", borderColor: "oklch(86% 0.06 70)" }}>
                <div className="flow__node-title">beacon.query_execution_history</div>
                <div className="flow__node-sub">source table</div>
              </div>
              <div className="flow__edge"></div>
              <div className="flow__node flow__node--db">
                <div className="flow__node-title">STEP 1 · TEST</div>
                <div className="flow__node-sub">postgres · select *</div>
              </div>
              <div className="flow__edge"></div>
              <div className="flow__node flow__node--result">
                <div className="flow__node-title">@result1</div>
                <div className="flow__node-sub">final output</div>
              </div>
            </div>
          </div>

          {/* Version history — cleaner empty state */}
          <div className="card">
            <div className="card__head">
              <Icon.Clock size={15} className="muted" />
              <h3 className="card__title">Version history</h3>
              <span className="card__sub">0 versions</span>
            </div>
            <div className="card__body">
              <div className="empty-state">
                <div className="empty-state__icon"><Icon.Clock size={20} /></div>
                <div>
                  <div className="empty-state__title">No versions recorded yet</div>
                  <div className="empty-state__sub">Versions are saved automatically whenever query SQL is modified.</div>
                </div>
              </div>
            </div>
          </div>

          {/* Subscriptions — cleaner empty state with table chrome */}
          <div className="card">
            <div className="card__head">
              <Icon.Inbox size={15} className="muted" />
              <h3 className="card__title">Subscriptions</h3>
              <span className="card__sub">0 attached</span>
              <div className="card__actions">
                <button className="btn btn--primary"><Icon.Plus size={13} className="btn__icon" /> Add subscription</button>
              </div>
            </div>
            <div className="card__body card__body--flush">
              <div className="tbl">
                <div className="tbl__head">
                  <div>Subscription ID</div>
                  <div>Name</div>
                  <div>Created</div>
                  <div>Subscribers</div>
                  <div>Cron expression</div>
                  <div></div>
                </div>
                <div className="empty-state" style={{ padding: "32px 16px" }}>
                  <div className="empty-state__icon"><Icon.Inbox size={20} /></div>
                  <div>
                    <div className="empty-state__title">No subscriptions yet</div>
                    <div className="empty-state__sub">Add a subscription to schedule this query and notify recipients.</div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <aside className="q-aside">
          <div className="card">
            <div className="card__head">
              <Icon.Activity size={15} className="muted" />
              <h3 className="card__title">Run history</h3>
              <span className="card__sub">last 30d</span>
            </div>
            <div className="card__body">
              <div className="empty-state" style={{ padding: 0, gap: 8 }}>
                <div className="empty-state__icon" style={{ width: 28, height: 28 }}><Icon.Bolt size={14} /></div>
                <div>
                  <div className="empty-state__title" style={{ fontSize: 12.5 }}>Never executed</div>
                  <div className="empty-state__sub" style={{ fontSize: 11.5 }}>Hit Execute query to populate metrics.</div>
                </div>
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Check size={15} className="muted" />
              <h3 className="card__title">Pre-flight checks</h3>
              <div className="card__actions">
                <span className="pill pill--ok">all clear</span>
              </div>
            </div>
            <div className="checks">
              <Check tone="ok" title="Source reachable" detail="test · 38ms" time="now" />
              <Check tone="ok" title="SQL parses" detail="postgres dialect" time="now" />
              <Check tone="ok" title="Permissions valid" detail="ro · beacon schema" time="now" />
              <Check tone="pending" title="Run to validate output" detail="awaiting first execution" />
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Users size={15} className="muted" />
              <h3 className="card__title">Recipients</h3>
              <span className="card__sub">0 hooked</span>
            </div>
            <div className="card__body" style={{ fontSize: 12.5, color: "var(--text-muted)" }}>
              Add a subscription to route results to recipients via email, Slack, or webhook.
            </div>
          </div>

          <div className="callout">
            <Icon.Lightbulb size={16} className="callout__icon" />
            <div>
              <div className="callout__title">Tip · faster iteration</div>
              <div className="callout__sub">Use <span className="kbd">⌘</span> <span className="kbd">↵</span> in the SQL editor to run just the active step without saving.</div>
            </div>
          </div>
        </aside>
      </div>

      <div className="save-bar">
        <span className="save-bar__hint">
          <span className="pill pill--ok"><span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--ok)" }}></span>SAVED</span>
          <span>All changes synced <span className="mono">just now</span>.</span>
        </span>
        <div className="spacer"></div>
        <span className="save-bar__hint">
          <span>Press</span>
          <span className="kbd">⌘</span><span className="kbd">↵</span>
          <span>to execute</span>
        </span>
        <button className="btn"><Icon.Sliders size={14} className="btn__icon" /> Edit SQL</button>
        <button className="btn btn--primary"><Icon.Bolt size={14} className="btn__icon" /> Execute query</button>
      </div>
    </div>
  );
}

function KV({ label, value }) {
  return (
    <div className="kv__row">
      <span className="kv__label">{label}</span>
      <span className="kv__value">{value}</span>
    </div>
  );
}

function SimpleSql() {
  return (
    <div className="sql" style={{ gridTemplateColumns: "1fr" }}>
      <div className="sql__editor">
        <div className="sql__toolbar">
          <span className="sql__tab"><Icon.Bolt size={11} /> step1.sql</span>
          <span className="subtle" style={{ marginLeft: "auto" }}>postgres · ro</span>
          <span className="subtle">·</span>
          <span>line 1, col 38</span>
        </div>
        <div className="sql__code">
          <div className="sql__gutter"><span>1</span></div>
          <pre className="sql__pre">
            <div>
              <span className="tok-kw">SELECT</span><span> * </span>
              <span className="tok-kw">FROM</span><span> beacon.query_execution_history</span>
              <span className="sql__caret">&nbsp;</span>
            </div>
          </pre>
        </div>
        <div className="sql__statusbar">
          <span className="ok">● parsed</span>
          <span>1 line · 47 chars</span>
          <span style={{ marginLeft: "auto" }}>0 params · 0 @result refs</span>
          <span className="kbd" style={{ fontSize: 10 }}>⌘</span>
          <span className="kbd" style={{ fontSize: 10 }}>↵</span>
          <span>run</span>
        </div>
      </div>
    </div>
  );
}

window.QueryDetailPage = QueryDetailPage;
