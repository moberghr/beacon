function TaskDetailPage() {
  const [tab, setTab] = React.useState("history");
  const [comment, setComment] = React.useState("");

  return (
    <div className="page" data-screen-label="04 Task Detail">
      <PageHeader
        variant="signal"
        eyebrow={
          <>
            <a href="#" style={{ color: "inherit", textDecoration: "none" }}>Tasks</a>
            <span className="beacon-hero__sep">/</span>
            <span className="mono">#1</span>
            <span className="beacon-hero__sep">·</span>
            <span className="badge badge--warn"><span className="badge__dot"></span>OPEN</span>
            <span className="beacon-hero__sep">·</span>
            <span className="badge mono" style={{ fontSize: 10 }}>USER-DEFINED</span>
          </>
        }
        prefix="Investigating"
        emphasis="qeh select"
        suffix=""
        sub={
          <>
            Triggered by subscription <span className="mono">qeh select</span> · created <span className="mono">05 May 2026 · 13:08</span> · open for <span className="mono">21h 14m</span>
          </>
        }
        actions={
          <>
            <button className="btn"><Icon.Bell size={14} className="btn__icon" /> Snooze</button>
            <button className="btn"><Icon.Users size={14} className="btn__icon" /> Assign</button>
            <button className="btn btn--resolve"><Icon.Check size={14} className="btn__icon" /> Resolve task</button>
          </>
        }
      />

      {/* Honest, calm KPIs replacing the colored tiles */}
      <div className="kpi-grid">
        <KPI dot="info" label="Latest result count" value="1" sub={<span className="badge badge--info">latest run</span>} />
        <KPI dot="ok" label="Executions" value="2" sub="2 succeeded · 0 errored" />
        <KPI dot="warn" label="Notifications" value="0" sub={<span className="badge badge--warn">none sent yet</span>} />
        <KPI dot="brand" label="Task age" value="21h" unit="14m" sub={<span className="mono subtle">since 05 May · 13:08</span>} />
      </div>

      <div className="row row--7-3">
        <div className="card">
          <div className="card__head">
            <Icon.Activity size={15} className="muted" />
            <h3 className="card__title">Result count progression</h3>
            <span className="card__sub">last 7 days · per execution</span>
            <div className="card__actions">
              <div className="seg">
                <button className="seg__btn">24h</button>
                <button className="seg__btn active">7d</button>
                <button className="seg__btn">30d</button>
              </div>
              <button className="btn btn--ghost btn--icon"><Icon.Download size={14} /></button>
            </div>
          </div>
          <div className="chart-wrap">
            <TaskResultChart />
            <div className="legend" style={{ marginTop: 4, paddingLeft: 4 }}>
              <span className="legend__item"><span className="legend__sw" style={{ background: "var(--brand-500)" }}></span>Result count</span>
              <span className="legend__item"><span className="legend__sw" style={{ background: "var(--warn)", opacity: 0.5 }}></span>Threshold</span>
              <span className="muted" style={{ marginLeft: "auto", fontSize: 11.5, fontFamily: "var(--font-mono)" }}>2 samples · alerts when count &gt; 0</span>
            </div>
          </div>
        </div>

        <div className="card">
          <div className="card__head">
            <Icon.Info size={15} className="muted" />
            <h3 className="card__title">Status</h3>
            <div className="card__actions">
              <span className="badge badge--warn"><span className="badge__dot"></span>open</span>
            </div>
          </div>
          <div className="card__body" style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <div className="banner banner--warn">
              <Icon.Alert size={14} className="banner__icon" />
              <div className="banner__main">
                <div className="banner__title">Awaiting human review</div>
                <div className="banner__sub">No notifications have been delivered yet. Resolve to mark the alert as handled.</div>
              </div>
            </div>
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <Step done label="Subscription fired" time="05 May · 13:08:30" />
              <Step done label="Query executed" time="05 May · 13:08:34" detail="3.53 ms" />
              <Step done label="Result threshold crossed" time="05 May · 13:08:34" detail="1 row > 0" />
              <Step active label="Notification delivery" detail="paused — recipients not yet wired" />
              <Step label="Human resolution" />
            </div>
          </div>
        </div>
      </div>

      {/* Task information — clean DL grid */}
      <div className="card">
        <div className="card__head">
          <Icon.Info size={15} className="muted" />
          <h3 className="card__title">Task information</h3>
          <div className="card__actions">
            <button className="btn btn--ghost btn--sm"><Icon.Cog size={13} className="btn__icon" /> Settings</button>
          </div>
        </div>
        <div className="card__body">
          <div className="dl-grid">
            <DL label="Task ID" value={<>#1</>} />
            <DL label="Subscription" value={<><Icon.Inbox size={12} /> qeh select</>} link />
            <DL label="Resolution status" value={<><span className="badge badge--warn"><span className="badge__dot"></span>not yet resolved</span></>} text />
            <DL label="Created" value="2026-05-05 13:08:34 UTC" />
            <DL label="Query" value={<><Icon.Query size={12} /> qeh select</>} link />
            <DL label="Last notification" value={<span className="subtle">none sent yet</span>} text />
            <DL label="Triggered by" value="cron · 0 */15 * * *" />
            <DL label="Owner" value="mirko · admin" text />
            <DL label="Priority" value={<><span className="badge badge--info">P2 · normal</span></>} text />
          </div>
        </div>
      </div>

      {/* Tabs: Notifications / Execution history / Related tasks */}
      <div className="card">
        <div className="tabs">
          <button className={"tab" + (tab === "notif" ? " active" : "")} onClick={() => setTab("notif")}>
            <Icon.Bell size={13} /> Notifications <span className="tab__count">0</span>
          </button>
          <button className={"tab" + (tab === "history" ? " active" : "")} onClick={() => setTab("history")}>
            <Icon.Clock size={13} /> Execution history <span className="tab__count">2</span>
          </button>
          <button className={"tab" + (tab === "related" ? " active" : "")} onClick={() => setTab("related")}>
            <Icon.Branch size={13} /> Related tasks <span className="tab__count">0</span>
          </button>
          <div style={{ marginLeft: "auto", padding: "6px 16px 6px 0", display: "flex", gap: 6 }}>
            <button className="btn btn--ghost btn--sm"><Icon.Refresh size={13} className="btn__icon" /> Refresh</button>
          </div>
        </div>

        {tab === "history" && (
          <table className="dtable">
            <thead>
              <tr>
                <th style={{ width: 70 }}>ID</th>
                <th className="is-sorted">Executed at <span className="sort-arrow">↓</span></th>
                <th className="num">Duration</th>
                <th className="num">Result count</th>
                <th>Status</th>
                <th style={{ width: 60 }}></th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td className="mono">#2</td>
                <td className="mono">2026-05-05 13:08:34</td>
                <td className="num">3.53 ms</td>
                <td className="num">1</td>
                <td><span className="badge badge--ok"><span className="badge__dot"></span>NotificationSent</span></td>
                <td><div className="row-actions"><button className="icon-btn"><Icon.Dots size={14} /></button></div></td>
              </tr>
              <tr>
                <td className="mono">#1</td>
                <td className="mono">2026-05-05 13:08:30</td>
                <td className="num">1.28 ms</td>
                <td className="num">0</td>
                <td><span className="badge"><span className="badge__dot"></span>NoResults</span></td>
                <td><div className="row-actions"><button className="icon-btn"><Icon.Dots size={14} /></button></div></td>
              </tr>
            </tbody>
          </table>
        )}

        {tab === "notif" && (
          <div className="card__body">
            <div className="empty-state">
              <div className="empty-state__icon"><Icon.Bell size={20} /></div>
              <div>
                <div className="empty-state__title">No notifications delivered yet</div>
                <div className="empty-state__sub">Wire recipients on the subscription to start sending email, Slack, or webhook notifications.</div>
              </div>
              <button className="btn btn--primary btn--sm" style={{ marginLeft: "auto" }}>
                <Icon.Plus size={13} className="btn__icon" /> Add recipient
              </button>
            </div>
          </div>
        )}

        {tab === "related" && (
          <div className="card__body">
            <div className="empty-state">
              <div className="empty-state__icon"><Icon.Branch size={20} /></div>
              <div>
                <div className="empty-state__title">No related tasks</div>
                <div className="empty-state__sub">Tasks from the same subscription within a 24h window appear here.</div>
              </div>
            </div>
          </div>
        )}
      </div>

      {/* Comments */}
      <div className="card">
        <div className="card__head">
          <Icon.Inbox size={15} className="muted" />
          <h3 className="card__title">Comments</h3>
          <span className="card__sub">0 · investigation log</span>
        </div>
        <div className="card__body" style={{ display: "flex", flexDirection: "column", gap: 14 }}>
          <div className="composer">
            <div className="avatar">MR</div>
            <div className="composer__main">
              <textarea
                className="composer__input"
                placeholder="Leave a note for whoever picks this up next… use @ to mention a teammate."
                value={comment}
                onChange={(e) => setComment(e.target.value)}
              />
              <div className="composer__bar">
                <div className="composer__tools">
                  <button className="icon-btn"><Icon.Plus size={14} /></button>
                  <button className="icon-btn"><Icon.Sliders size={14} /></button>
                  <button className="icon-btn"><Icon.Lightbulb size={14} /></button>
                </div>
                <span className="muted" style={{ fontSize: 11.5 }}>Markdown supported · <span className="mono">{comment.length}/2000</span></span>
                <div style={{ marginLeft: "auto", display: "flex", gap: 6 }}>
                  <button className="btn btn--ghost btn--sm">Cancel</button>
                  <button className="btn btn--primary btn--sm" disabled={!comment.length}>
                    <Icon.Plus size={13} className="btn__icon" /> Post comment
                  </button>
                </div>
              </div>
            </div>
          </div>
          <div className="empty-state" style={{ padding: 14 }}>
            <div className="empty-state__icon"><Icon.Inbox size={18} /></div>
            <div>
              <div className="empty-state__title">No comments yet</div>
              <div className="empty-state__sub">Be the first to add one — investigation notes show up here in chronological order.</div>
            </div>
          </div>
        </div>
      </div>

      {/* Sticky save bar */}
      <div className="save-bar">
        <span className="save-bar__hint">
          <span className="badge badge--warn"><span className="badge__dot"></span>OPEN</span>
          <span>Created <span className="mono">21h 14m</span> ago.</span>
        </span>
        <div className="spacer"></div>
        <button className="btn"><Icon.Bell size={14} className="btn__icon" /> Snooze 1h</button>
        <button className="btn"><Icon.Users size={14} className="btn__icon" /> Assign to me</button>
        <button className="btn btn--resolve"><Icon.Check size={14} className="btn__icon" /> Resolve task</button>
      </div>
    </div>
  );
}

function Step({ done, active, label, time, detail }) {
  return (
    <div style={{ display: "flex", alignItems: "flex-start", gap: 10 }}>
      <div style={{
        width: 18, height: 18,
        borderRadius: 99,
        flexShrink: 0,
        marginTop: 1,
        background: done ? "var(--ok)" : active ? "var(--warn-bg)" : "var(--surface-2)",
        border: "1.5px solid " + (done ? "var(--ok)" : active ? "var(--warn)" : "var(--border-strong)"),
        display: "grid", placeItems: "center",
        color: "white",
      }}>
        {done && <Icon.Check size={10} stroke={2.5} />}
        {active && <span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--warn)" }}></span>}
      </div>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 13, color: "var(--text)", fontWeight: done || active ? 500 : 400 }}>
          {label}
          {detail && <span className="mono subtle" style={{ marginLeft: 8, fontSize: 11.5 }}>· {detail}</span>}
        </div>
        {time && <div className="mono subtle" style={{ fontSize: 11 }}>{time}</div>}
      </div>
    </div>
  );
}

function DL({ label, value, link, text }) {
  return (
    <div className="dl-cell">
      <span className="dl-cell__label">{label}</span>
      <span className={"dl-cell__value" + (text ? " dl-cell__value--text" : "")}>
        {value}
        {link && <Icon.Chevron size={11} className="subtle" style={{ marginLeft: "auto" }} />}
      </span>
    </div>
  );
}

function TaskResultChart() {
  const w = 720, h = 200;
  const points = [
    { x: 60, y: 170, label: "13:08:30", val: 0 },
    { x: 660, y: 50, label: "13:08:34", val: 1 },
  ];
  return (
    <svg viewBox={`0 0 ${w} ${h}`} width="100%" height="200">
      <defs>
        <linearGradient id="task-area" x1="0" x2="0" y1="0" y2="1">
          <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.20" />
          <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
        </linearGradient>
      </defs>
      {/* Threshold line at y=120 (count=0.5) */}
      <line x1="40" x2={w - 20} y1="120" y2="120" stroke="var(--warn)" strokeOpacity="0.4" strokeWidth="1" strokeDasharray="3 4" />
      <text x={w - 24} y="115" fontFamily="var(--font-mono)" fontSize="9" fill="var(--warn)" textAnchor="end">threshold = 0</text>
      {/* y axis labels */}
      {[0, 1, 2, 3, 4, 5].map((v) => (
        <g key={v}>
          <line x1="40" x2={w - 20} y1={170 - v * 24} y2={170 - v * 24} stroke="var(--border)" strokeOpacity="0.6" />
          <text x="32" y={174 - v * 24} fontFamily="var(--font-mono)" fontSize="10" fill="var(--text-subtle)" textAnchor="end">{v}</text>
        </g>
      ))}
      {/* area */}
      <path d={`M ${points[0].x} 170 L ${points[0].x} ${points[0].y} L ${points[1].x} ${points[1].y} L ${points[1].x} 170 Z`} fill="url(#task-area)" />
      <path d={`M ${points[0].x} ${points[0].y} L ${points[1].x} ${points[1].y}`} stroke="var(--brand-500)" strokeWidth="2" fill="none" strokeLinecap="round" />
      {points.map((p, i) => (
        <g key={i}>
          <circle cx={p.x} cy={p.y} r="5" fill="var(--surface)" stroke="var(--brand-500)" strokeWidth="2" />
          <text x={p.x} y={p.y - 12} fontFamily="var(--font-mono)" fontSize="11" fill="var(--text)" textAnchor="middle" fontWeight="500">{p.val}</text>
          <text x={p.x} y={h - 10} fontFamily="var(--font-mono)" fontSize="10" fill="var(--text-subtle)" textAnchor="middle">{p.label}</text>
        </g>
      ))}
    </svg>
  );
}

window.TaskDetailPage = TaskDetailPage;
