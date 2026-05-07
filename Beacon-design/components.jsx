function ComponentsPage() {
  const [text, setText] = React.useState("");
  const [select, setSelect] = React.useState("postgres");
  const [seg, setSeg] = React.useState("active");
  const [check1, setCheck1] = React.useState(true);
  const [check2, setCheck2] = React.useState(false);
  const [radio, setRadio] = React.useState("email");
  const [toggle1, setToggle1] = React.useState(true);
  const [toggle2, setToggle2] = React.useState(false);
  const [tab, setTab] = React.useState("table");
  const [tags, setTags] = React.useState(["mirko", "data-team"]);
  const [tagInput, setTagInput] = React.useState("");

  const removeTag = (t) => setTags(tags.filter(x => x !== t));
  const addTag = (e) => {
    if (e.key === "Enter" && tagInput.trim()) {
      setTags([...tags, tagInput.trim()]);
      setTagInput("");
    }
  };

  return (
    <div className="page ds-page" data-screen-label="05 Components">
      <PageHeader
        variant="grid"
        eyebrow={
          <>
            <span>Design system</span>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">v0.99</span>
            <span className="beacon-hero__sep">·</span>
            <span className="badge badge--brand">REFERENCE</span>
          </>
        }
        prefix="Reusable"
        emphasis="components"
        suffix="for admin systems."
        sub={<>Pick a primitive and drop it into any Beacon screen. Every control here is wired to live state — interact to see hover, focus, and selected states.</>}
        actions={
          <>
            <button className="btn"><Icon.Download size={14} className="btn__icon" /> Export tokens</button>
            <button className="btn btn--primary"><Icon.Plus size={14} className="btn__icon" /> New component</button>
          </>
        }
      />

      {/* Color tokens */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">01</span>
          <h2 className="ds-section__title">Color tokens</h2>
          <span className="ds-section__sub">brand · neutral · status — referenced as CSS variables</span>
        </div>
        <div className="ds-grid">
          <Swatch name="brand-500" hex="oklch(58% .095 175)" cssVar="--brand-500" />
          <Swatch name="brand-700" hex="oklch(42% .08 175)" cssVar="--brand-700" />
          <Swatch name="ok" hex="oklch(62% .13 155)" cssVar="--ok" />
          <Swatch name="warn" hex="oklch(70% .15 70)" cssVar="--warn" />
          <Swatch name="crit" hex="oklch(60% .19 25)" cssVar="--crit" />
          <Swatch name="info" hex="oklch(60% .13 240)" cssVar="--info" />
          <Swatch name="surface" hex="—" cssVar="--surface" border />
          <Swatch name="surface-2" hex="—" cssVar="--surface-2" border />
        </div>
      </section>

      {/* Buttons */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">02</span>
          <h2 className="ds-section__title">Buttons</h2>
          <span className="ds-section__sub">primary · subtle · danger · icon · groups</span>
        </div>

        <div className="ds-block">
          <DSHead title="Variants" hint=".btn / .btn--primary / .btn--subtle / .btn--danger" />
          <div className="ds-block__body">
            <div className="ds-block__row">
              <button className="btn btn--primary"><Icon.Bolt size={14} className="btn__icon" /> Execute query</button>
              <button className="btn"><Icon.Plus size={14} className="btn__icon" /> Add step</button>
              <button className="btn btn--subtle"><Icon.Sliders size={14} className="btn__icon" /> Configure</button>
              <button className="btn btn--ghost"><Icon.Refresh size={14} className="btn__icon" /> Refresh</button>
              <button className="btn btn--danger"><Icon.X size={14} className="btn__icon" /> Delete</button>
              <button className="btn" disabled><Icon.Lock size={14} className="btn__icon" /> Disabled</button>
            </div>
            <div className="ds-block__row">
              <button className="btn btn--primary btn--sm">Small</button>
              <button className="btn btn--primary">Default</button>
              <button className="btn btn--primary btn--lg">Large</button>
              <button className="btn btn--icon" title="Settings"><Icon.Cog size={14} /></button>
              <button className="icon-btn"><Icon.Bell size={16} /></button>
            </div>
            <div className="ds-block__row">
              <div className="btn-group">
                <button className="btn is-on"><Icon.Grid size={13} className="btn__icon" /> Grid</button>
                <button className="btn"><Icon.Layers size={13} className="btn__icon" /> List</button>
                <button className="btn"><Icon.Calendar size={13} className="btn__icon" /> Calendar</button>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Form fields */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">03</span>
          <h2 className="ds-section__title">Form fields</h2>
          <span className="ds-section__sub">text · textarea · select · search · numeric · with affixes</span>
        </div>

        <div className="ds-block">
          <DSHead title="Text inputs" hint=".field + .input / .textarea / .select" />
          <div className="ds-block__body">
            <div className="row row--2-up">
              <div className="field">
                <label className="field__label">
                  Query name <span className="field__req">*</span>
                </label>
                <input className="input" placeholder="e.g. EU signups vs revenue — daily" value={text} onChange={(e) => setText(e.target.value)} />
                <span className="field__hint">A short, descriptive name. Shown in lists and notifications.</span>
              </div>
              <div className="field">
                <label className="field__label">
                  Cron schedule <span className="field__opt">optional</span>
                </label>
                <div className="input-wrap input-wrap--has-suffix">
                  <span className="input-wrap__icon"><Icon.Clock size={14} /></span>
                  <input className="input mono" defaultValue="0 */15 * * *" />
                  <span className="input-wrap__suffix">UTC</span>
                </div>
                <span className="field__hint">Runs every 15 minutes. Leave empty to run on demand.</span>
              </div>
              <div className="field">
                <label className="field__label">Data source</label>
                <select className="select" value={select} onChange={(e) => setSelect(e.target.value)}>
                  <option value="postgres">test (PostgreSQL · 38ms)</option>
                  <option value="bigquery">analytics-prod (BigQuery)</option>
                  <option value="clickhouse">events-ch (ClickHouse)</option>
                </select>
              </div>
              <div className="field">
                <label className="field__label">
                  Slack webhook <span className="field__req">*</span>
                </label>
                <input className="input input--error" defaultValue="https://hooks.slack.com/services/INVALID" />
                <span className="field__error"><Icon.Alert size={11} /> Webhook returned 404 — check that the channel still exists.</span>
              </div>
              <div className="field" style={{ gridColumn: "1 / -1" }}>
                <label className="field__label">Description</label>
                <textarea className="textarea" placeholder="What does this query measure, and why?" />
                <span className="field__hint">Supports markdown · 0/280 characters</span>
              </div>
              <div className="field">
                <label className="field__label">Search</label>
                <div className="input-wrap">
                  <span className="input-wrap__icon"><Icon.Search size={14} /></span>
                  <input className="input" placeholder="Search recipients…" />
                </div>
              </div>
              <div className="field">
                <label className="field__label">Tags</label>
                <div className="tag-input">
                  {tags.map(t => (
                    <span key={t} className="tag">
                      {t}
                      <button className="tag__x" onClick={() => removeTag(t)}><Icon.X size={10} /></button>
                    </span>
                  ))}
                  <input value={tagInput} onChange={(e) => setTagInput(e.target.value)} onKeyDown={addTag} placeholder="Add tag…" />
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Selection controls */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">04</span>
          <h2 className="ds-section__title">Selection</h2>
          <span className="ds-section__sub">checkbox · radio · toggle · segmented</span>
        </div>

        <div className="row row--2-up">
          <div className="ds-block">
            <DSHead title="Checkbox & radio" hint=".check-row + .cbox / .rbox" />
            <div className="ds-block__body">
              <label className="check-row" onClick={() => setCheck1(!check1)}>
                <span className={"cbox" + (check1 ? " is-on" : "")}></span>
                <div className="check-row__main">
                  <div className="check-row__title">Send Slack notifications</div>
                  <div className="check-row__sub">Posts to #data-alerts when this query returns rows.</div>
                </div>
              </label>
              <label className="check-row" onClick={() => setCheck2(!check2)}>
                <span className={"cbox" + (check2 ? " is-on" : "")}></span>
                <div className="check-row__main">
                  <div className="check-row__title">Mention the on-call engineer</div>
                  <div className="check-row__sub">Adds @oncall to the Slack message during business hours.</div>
                </div>
              </label>
              <div className="skel-line" style={{ margin: "8px 0" }}></div>
              {[
                { v: "email", t: "Email", s: "Sends to all subscribed recipients." },
                { v: "slack", t: "Slack", s: "Posts to a channel webhook." },
                { v: "webhook", t: "Custom webhook", s: "POSTs JSON payload with results." },
              ].map(o => (
                <label key={o.v} className={"check-row check-row--card" + (radio === o.v ? " is-on" : "")} onClick={() => setRadio(o.v)} style={{ marginBottom: 6 }}>
                  <span className={"rbox" + (radio === o.v ? " is-on" : "")}></span>
                  <div className="check-row__main">
                    <div className="check-row__title">{o.t}</div>
                    <div className="check-row__sub">{o.s}</div>
                  </div>
                </label>
              ))}
            </div>
          </div>

          <div className="ds-block">
            <DSHead title="Toggles & segmented" hint=".toggle / .radio-seg / .seg" />
            <div className="ds-block__body">
              <div className="opt-row">
                <div className="opt-row__main">
                  <div className="opt-row__title">Auto-resolve when count returns to zero</div>
                  <div className="opt-row__sub">Closes open tasks the moment a follow-up run reports 0 rows.</div>
                </div>
                <button className={"toggle" + (toggle1 ? " toggle--on" : "")} onClick={() => setToggle1(!toggle1)}>
                  <span className="toggle__thumb"></span>
                </button>
              </div>
              <div className="opt-row">
                <div className="opt-row__main">
                  <div className="opt-row__title">Pause notifications during deploys</div>
                  <div className="opt-row__sub">Listens for deploy webhook · suppresses alerts for 10 minutes after.</div>
                </div>
                <button className={"toggle" + (toggle2 ? " toggle--on" : "")} onClick={() => setToggle2(!toggle2)}>
                  <span className="toggle__thumb"></span>
                </button>
              </div>
              <div className="skel-line" style={{ margin: "8px 0" }}></div>
              <div className="field">
                <label className="field__label">Status filter</label>
                <div className="radio-seg">
                  {["active", "paused", "archived"].map(v => (
                    <button key={v} className={"radio-seg__btn" + (seg === v ? " is-on" : "")} onClick={() => setSeg(v)}>
                      {seg === v && <span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--brand-500)" }}></span>}
                      {v}
                    </button>
                  ))}
                </div>
              </div>
              <div className="field">
                <label className="field__label">Time range</label>
                <div className="seg">
                  <button className="seg__btn">1h</button>
                  <button className="seg__btn">24h</button>
                  <button className="seg__btn active">7d</button>
                  <button className="seg__btn">30d</button>
                  <button className="seg__btn">90d</button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Badges + status */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">05</span>
          <h2 className="ds-section__title">Status indicators</h2>
          <span className="ds-section__sub">badges · pills · avatars · tooltips</span>
        </div>
        <div className="ds-block">
          <DSHead title="Badges" hint=".badge + .badge--ok|warn|crit|info|brand" />
          <div className="ds-block__body">
            <div className="ds-block__row">
              <span className="badge badge--ok"><span className="badge__dot"></span>active</span>
              <span className="badge badge--warn"><span className="badge__dot"></span>open</span>
              <span className="badge badge--crit"><span className="badge__dot"></span>failed</span>
              <span className="badge badge--info"><span className="badge__dot"></span>pending</span>
              <span className="badge badge--brand">USER-DEFINED</span>
              <span className="badge">archived</span>
              <span className="badge mono">v0.99</span>
              <span className="badge mono">P2 · normal</span>
            </div>
            <div className="ds-block__row">
              <span className="pill pill--ok">SAVED</span>
              <span className="pill pill--warn">DRAFT</span>
              <span className="pill pill--crit">ERRORED</span>
              <span className="pill pill--info">RUNNING</span>
              <span className="pill pill--neutral">UNSAVED</span>
            </div>
            <div className="ds-block__row">
              <div className="avatar avatar--sm">MR</div>
              <div className="avatar">MR</div>
              <div className="avatar avatar--lg">MR</div>
              <div className="avatar-stack">
                <div className="avatar avatar--sm" style={{ background: "linear-gradient(135deg, oklch(70% 0.13 25), oklch(50% 0.13 25))" }}>JD</div>
                <div className="avatar avatar--sm" style={{ background: "linear-gradient(135deg, oklch(70% 0.13 240), oklch(50% 0.13 240))" }}>SB</div>
                <div className="avatar avatar--sm">MR</div>
                <div className="avatar avatar--sm" style={{ background: "var(--surface-2)", color: "var(--text-muted)", fontWeight: 500 }}>+4</div>
              </div>
              <div className="tip-wrap">
                <button className="icon-btn"><Icon.Info size={16} /></button>
                <span className="tip">Hover tooltip example</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Banners */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">06</span>
          <h2 className="ds-section__title">Banners & callouts</h2>
          <span className="ds-section__sub">inline notices for state changes</span>
        </div>
        <div className="ds-block">
          <DSHead title="Banners" hint=".banner + .banner--ok|warn|crit|info" />
          <div className="ds-block__body ds-block__row--col">
            <div className="banner banner--ok">
              <Icon.Check size={14} className="banner__icon" />
              <div className="banner__main">
                <div className="banner__title">Query saved</div>
                <div className="banner__sub">All steps validated and persisted to <span className="mono">beacon.queries</span>.</div>
              </div>
              <button className="banner__close"><Icon.X size={14} /></button>
            </div>
            <div className="banner banner--warn">
              <Icon.Alert size={14} className="banner__icon" />
              <div className="banner__main">
                <div className="banner__title">No recipients on this subscription</div>
                <div className="banner__sub">Notifications will not be delivered until you add at least one recipient.</div>
              </div>
              <button className="btn btn--sm">Fix it</button>
            </div>
            <div className="banner banner--crit">
              <Icon.X size={14} className="banner__icon" />
              <div className="banner__main">
                <div className="banner__title">Source unreachable</div>
                <div className="banner__sub"><span className="mono">analytics-prod</span> failed health check 3× in a row · last error <span className="mono">connection refused</span>.</div>
              </div>
              <button className="btn btn--sm">Retry</button>
            </div>
            <div className="banner banner--info">
              <Icon.Lightbulb size={14} className="banner__icon" />
              <div className="banner__main">
                <div className="banner__title">Tip · faster iteration</div>
                <div className="banner__sub">Use <span className="kbd">⌘</span> <span className="kbd">↵</span> in any SQL editor to run just the active step.</div>
              </div>
            </div>
            <div className="callout">
              <Icon.Lightbulb size={16} className="callout__icon" />
              <div>
                <div className="callout__title">Callout · brand accent</div>
                <div className="callout__sub">For longer-form tips inside cards. Uses a left brand stripe.</div>
              </div>
              <button className="callout__action">Learn more →</button>
            </div>
          </div>
        </div>
      </section>

      {/* Cards & containers */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">07</span>
          <h2 className="ds-section__title">Cards & containers</h2>
          <span className="ds-section__sub">card · KPI · stat-grid · key/value</span>
        </div>

        <div className="row row--2-up">
          <div className="kpi">
            <div className="kpi__head">
              <span className="kpi__dot kpi__dot--ok"></span>
              <span className="kpi__label">Active queries</span>
              <span className="kpi__menu"><Icon.Dots size={13} /></span>
            </div>
            <div className="kpi__value">38<span className="kpi__unit">/40</span></div>
            <div className="kpi__sub">
              <span className="kpi__delta"><Icon.ArrowUp size={11} /> +4</span>
              <span>vs last week</span>
            </div>
          </div>
          <div className="kpi">
            <div className="kpi__head">
              <span className="kpi__dot kpi__dot--warn"></span>
              <span className="kpi__label">Open tasks</span>
              <span className="kpi__menu"><Icon.Dots size={13} /></span>
            </div>
            <div className="kpi__value">7</div>
            <div className="kpi__sub">
              <span className="kpi__delta kpi__delta--down"><Icon.ArrowDown size={11} /> −2</span>
              <span>resolved today</span>
            </div>
          </div>
        </div>

        <div className="ds-block">
          <DSHead title="Stat grid" hint=".stat-grid + .stat — for read-only summaries" />
          <div className="ds-block__body">
            <div className="stat-grid">
              <div className="stat">
                <span className="stat__label">Latest result</span>
                <span className="stat__value">1</span>
                <span className="stat__sub">at 13:08:34</span>
              </div>
              <div className="stat">
                <span className="stat__label">Executions</span>
                <span className="stat__value">2</span>
                <span className="stat__sub">2 ok · 0 errored</span>
              </div>
              <div className="stat">
                <span className="stat__label">Notifications</span>
                <span className="stat__value">0</span>
                <span className="stat__sub">none sent yet</span>
              </div>
              <div className="stat">
                <span className="stat__label">Task age</span>
                <span className="stat__value">21h</span>
                <span className="stat__sub">since 05 May 13:08</span>
              </div>
            </div>
          </div>
        </div>

        <div className="ds-block">
          <DSHead title="Definition list" hint=".dl-grid + .dl-cell — calm replacement for colored info bars" />
          <div className="ds-block__body">
            <div className="dl-grid">
              <div className="dl-cell">
                <span className="dl-cell__label">Task ID</span>
                <span className="dl-cell__value">#1</span>
              </div>
              <div className="dl-cell">
                <span className="dl-cell__label">Subscription</span>
                <span className="dl-cell__value"><Icon.Inbox size={12} /> qeh select</span>
              </div>
              <div className="dl-cell">
                <span className="dl-cell__label">Resolution</span>
                <span className="dl-cell__value dl-cell__value--text"><span className="badge badge--warn"><span className="badge__dot"></span>not yet resolved</span></span>
              </div>
              <div className="dl-cell">
                <span className="dl-cell__label">Created</span>
                <span className="dl-cell__value">2026-05-05 13:08:34 UTC</span>
              </div>
              <div className="dl-cell">
                <span className="dl-cell__label">Query</span>
                <span className="dl-cell__value"><Icon.Query size={12} /> qeh select</span>
              </div>
              <div className="dl-cell">
                <span className="dl-cell__label">Owner</span>
                <span className="dl-cell__value dl-cell__value--text">mirko · admin</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Tables, tabs, pagination */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">08</span>
          <h2 className="ds-section__title">Tables, tabs & pagination</h2>
          <span className="ds-section__sub">data tables with sortable headers, hover row actions, pagination</span>
        </div>

        <div className="card">
          <div className="tabs">
            <button className={"tab" + (tab === "table" ? " active" : "")} onClick={() => setTab("table")}>
              <Icon.Layers size={13} /> Recent runs <span className="tab__count">12</span>
            </button>
            <button className={"tab" + (tab === "empty" ? " active" : "")} onClick={() => setTab("empty")}>
              <Icon.Bell size={13} /> Notifications <span className="tab__count">0</span>
            </button>
            <button className={"tab" + (tab === "loading" ? " active" : "")} onClick={() => setTab("loading")}>
              <Icon.Refresh size={13} /> Logs
            </button>
          </div>

          {tab === "table" && (
            <>
              <table className="dtable">
                <thead>
                  <tr>
                    <th style={{ width: 36 }}><span className="cbox" style={{ margin: 0 }}></span></th>
                    <th style={{ width: 80 }}>ID</th>
                    <th className="is-sorted">Executed at <span className="sort-arrow">↓</span></th>
                    <th className="num">Duration</th>
                    <th className="num">Result count</th>
                    <th>Status</th>
                    <th>Owner</th>
                    <th style={{ width: 60 }}></th>
                  </tr>
                </thead>
                <tbody>
                  {[
                    { id: 12, t: "2026-05-05 13:08:34", d: "3.53", r: 1, st: "ok", stl: "NotificationSent", who: "MR" },
                    { id: 11, t: "2026-05-05 13:08:30", d: "1.28", r: 0, st: "neutral", stl: "NoResults", who: "MR", sel: true },
                    { id: 10, t: "2026-05-05 12:53:12", d: "4.10", r: 2, st: "ok", stl: "NotificationSent", who: "MR" },
                    { id: 9,  t: "2026-05-05 12:38:01", d: "—",   r: "—", st: "crit", stl: "ConnectionRefused", who: "system" },
                    { id: 8,  t: "2026-05-05 12:23:00", d: "2.97", r: 0, st: "neutral", stl: "NoResults", who: "MR" },
                  ].map(row => (
                    <tr key={row.id} className={row.sel ? "is-selected" : ""}>
                      <td><span className={"cbox" + (row.sel ? " is-on" : "")} style={{ margin: 0 }}></span></td>
                      <td className="mono">#{row.id}</td>
                      <td className="mono">{row.t}</td>
                      <td className="num">{row.d} ms</td>
                      <td className="num">{row.r}</td>
                      <td><span className={"badge badge--" + (row.st === "neutral" ? "" : row.st)}><span className="badge__dot"></span>{row.stl}</span></td>
                      <td><div className="avatar avatar--sm">{row.who}</div></td>
                      <td>
                        <div className="row-actions">
                          <button className="icon-btn"><Icon.Bolt size={14} /></button>
                          <button className="icon-btn"><Icon.Dots size={14} /></button>
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className="pagination">
                <span>Showing <span className="mono">1–5</span> of <span className="mono">12</span></span>
                <div className="spacer"></div>
                <select className="select" style={{ width: "auto", padding: "4px 24px 4px 8px", fontSize: 12 }}>
                  <option>10 / page</option>
                  <option>25 / page</option>
                  <option>50 / page</option>
                </select>
                <div className="pagination__pages">
                  <button className="pagination__page" disabled>‹</button>
                  <button className="pagination__page is-on">1</button>
                  <button className="pagination__page">2</button>
                  <button className="pagination__page">3</button>
                  <button className="pagination__page">›</button>
                </div>
              </div>
            </>
          )}

          {tab === "empty" && (
            <div className="card__body">
              <div className="empty-state">
                <div className="empty-state__icon"><Icon.Bell size={20} /></div>
                <div>
                  <div className="empty-state__title">No notifications delivered yet</div>
                  <div className="empty-state__sub">Wire recipients on the subscription to start sending alerts.</div>
                </div>
                <button className="btn btn--primary btn--sm" style={{ marginLeft: "auto" }}>
                  <Icon.Plus size={13} className="btn__icon" /> Add recipient
                </button>
              </div>
            </div>
          )}

          {tab === "loading" && (
            <div className="card__body" style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              {[80, 60, 90, 50, 75].map((w, i) => (
                <div key={i} style={{ display: "flex", alignItems: "center", gap: 10 }}>
                  <div style={{ width: 14, height: 14, borderRadius: 99, background: "var(--surface-2)" }}></div>
                  <div style={{ height: 12, width: w + "%", background: "var(--surface-2)", borderRadius: 4 }}></div>
                </div>
              ))}
              <span className="muted" style={{ fontSize: 12 }}>Skeleton loading state</span>
            </div>
          )}
        </div>
      </section>

      {/* Empty states */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">09</span>
          <h2 className="ds-section__title">Empty states</h2>
          <span className="ds-section__sub">honest, calm placeholders — never silent loaders</span>
        </div>
        <div className="row row--2-up">
          <div className="empty-state">
            <div className="empty-state__icon"><Icon.Inbox size={20} /></div>
            <div>
              <div className="empty-state__title">No subscriptions yet</div>
              <div className="empty-state__sub">Add a subscription to schedule this query and notify recipients.</div>
            </div>
            <button className="btn btn--primary btn--sm" style={{ marginLeft: "auto" }}>
              <Icon.Plus size={13} className="btn__icon" /> Add
            </button>
          </div>
          <div className="empty-state">
            <div className="empty-state__icon"><Icon.Clock size={20} /></div>
            <div>
              <div className="empty-state__title">No versions recorded yet</div>
              <div className="empty-state__sub">Versions save automatically when SQL changes.</div>
            </div>
          </div>
        </div>
      </section>

      {/* Comments / composer */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">10</span>
          <h2 className="ds-section__title">Comments & threads</h2>
          <span className="ds-section__sub">composer + reply timeline for investigation logs</span>
        </div>
        <div className="ds-block">
          <DSHead title="Thread" hint=".composer + .thread + .thread__item" />
          <div className="ds-block__body ds-block__row--col">
            <div className="composer">
              <div className="avatar">MR</div>
              <div className="composer__main">
                <textarea className="composer__input" placeholder="Leave a note for whoever picks this up next…" />
                <div className="composer__bar">
                  <div className="composer__tools">
                    <button className="icon-btn"><Icon.Plus size={14} /></button>
                    <button className="icon-btn"><Icon.Sliders size={14} /></button>
                    <button className="icon-btn"><Icon.Lightbulb size={14} /></button>
                  </div>
                  <span className="muted" style={{ fontSize: 11.5 }}>Markdown supported</span>
                  <div style={{ marginLeft: "auto" }}>
                    <button className="btn btn--primary btn--sm"><Icon.Plus size={13} className="btn__icon" /> Post</button>
                  </div>
                </div>
              </div>
            </div>
            <div className="thread">
              <div className="thread__item">
                <div className="avatar" style={{ background: "linear-gradient(135deg, oklch(70% 0.13 240), oklch(50% 0.13 240))" }}>JD</div>
                <div className="thread__bubble">
                  <div className="thread__head">
                    <span className="thread__name">Jamie Dee</span>
                    <span className="thread__meta">2h ago · edited</span>
                  </div>
                  <div className="thread__body">
                    Looks like the source returned a single row at 13:08 — same shape as the spike from last week. I'll dig into <span className="mono">beacon.query_execution_history</span> and report back.
                  </div>
                  <div className="thread__actions">
                    <button>Reply</button>
                    <button>React</button>
                    <button>Copy link</button>
                  </div>
                </div>
              </div>
              <div className="thread__item">
                <div className="avatar">MR</div>
                <div className="thread__bubble">
                  <div className="thread__head">
                    <span className="thread__name">mirko</span>
                    <span className="thread__meta">just now</span>
                  </div>
                  <div className="thread__body">
                    Acknowledged. I'll resolve once we confirm the row is expected.
                  </div>
                  <div className="thread__actions">
                    <button>Reply</button>
                    <button>React</button>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Modals reminder */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">11</span>
          <h2 className="ds-section__title">Dialogs</h2>
          <span className="ds-section__sub">use the modal system from <span className="mono">dialogs.jsx</span></span>
        </div>
        <div className="banner banner--info">
          <Icon.Info size={14} className="banner__icon" />
          <div className="banner__main">
            <div className="banner__title">Open <a href="Beacon Dialogs.html" style={{ color: "inherit" }}>Beacon Dialogs.html</a></div>
            <div className="banner__sub">Create-project and create-dashboard examples live there. Same hero pattern, dashed section dividers, mono metadata.</div>
          </div>
        </div>
      </section>

      {/* Index nav */}
      <section className="ds-section">
        <div className="ds-section__head">
          <span className="ds-section__num">12</span>
          <h2 className="ds-section__title">Where to use</h2>
          <span className="ds-section__sub">other Beacon screens already wired with these components</span>
        </div>
        <div className="ds-grid">
          <PageLink href="Beacon Dashboard.html" title="Dashboard" sub="Hero · KPIs · charts" icon="Home" />
          <PageLink href="Beacon Add Query.html" title="Add Query" sub="Forms · SQL editor · steps" icon="Query" />
          <PageLink href="Beacon Query Detail.html" title="Query Detail" sub="KV grid · run history · pre-flight" icon="Branch" />
          <PageLink href="Beacon Task Detail.html" title="Task Detail" sub="Tabs · table · comments" icon="Check" />
          <PageLink href="Beacon Dialogs.html" title="Dialogs" sub="Modal system" icon="Inbox" />
        </div>
      </section>
    </div>
  );
}

function DSHead({ title, hint }) {
  return (
    <div className="ds-block__head">
      <span className="ds-block__title">{title}</span>
      <span className="ds-block__hint">{hint}</span>
    </div>
  );
}

function Swatch({ name, hex, cssVar, border }) {
  return (
    <div className="ds-swatch">
      <div className="ds-swatch__chip" style={{ background: "var(" + cssVar + ")", borderColor: border ? "var(--border)" : undefined }}></div>
      <div style={{ minWidth: 0 }}>
        <div className="ds-swatch__name">{name}</div>
        <div className="ds-swatch__hex mono">{cssVar}</div>
      </div>
    </div>
  );
}

function PageLink({ href, title, sub, icon }) {
  const I = Icon[icon] || Icon.Folder;
  return (
    <a href={href} className="ds-swatch" style={{ textDecoration: "none", color: "inherit" }}>
      <div style={{ width: 38, height: 38, borderRadius: 6, background: "var(--brand-50)", color: "var(--brand-700)", display: "grid", placeItems: "center", border: "1px solid var(--brand-200)", flexShrink: 0 }}>
        <I size={16} />
      </div>
      <div style={{ minWidth: 0 }}>
        <div className="ds-swatch__name">{title}</div>
        <div className="ds-swatch__hex">{sub}</div>
      </div>
      <Icon.Chevron size={14} className="muted" style={{ marginLeft: "auto" }} />
    </a>
  );
}

window.ComponentsPage = ComponentsPage;
