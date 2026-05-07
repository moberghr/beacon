function Dashboard() {
  const sparkA = [3, 5, 4, 7, 6, 9, 8, 11, 10, 14, 13, 16, 15, 18];
  const sparkB = [12, 11, 14, 13, 16, 18, 17, 20, 19, 22, 24, 23, 26, 28];
  const sparkC = [2, 4, 3, 5, 8, 6, 9, 12, 10, 14, 11, 8, 6, 5];
  const sparkD = [0, 0, 1, 0, 2, 1, 3, 2, 4, 3, 5, 4, 7, 5];

  const trendQueries = [4, 7, 12, 9, 14, 18, 22, 19, 25, 30, 28, 34, 31, 38, 42, 39, 44, 48, 45, 52, 56, 51, 58, 62, 60, 66, 71, 68, 74, 78];
  const trendNotifs = [1, 2, 4, 3, 5, 7, 6, 9, 8, 11, 13, 10, 14, 12, 16, 15, 19, 17, 22, 20, 24, 21, 26, 24, 29, 27, 31, 28, 34, 36];

  const buckets = Array.from({ length: 30 }, (_, i) => {
    const base = 80 + Math.sin(i / 3) * 30 + Math.random() * 40;
    const p = i === 7 ? "p95" : i === 22 ? "p99" : "p50";
    return { label: `d-${29 - i}`, value: Math.round(base + (p === "p95" ? 200 : p === "p99" ? 400 : 0)), p };
  });

  return (
    <div className="page">
      {/* Page header */}
      <BeaconHeader />

      {/* Filter / range bar */}
      <div className="filter-bar">
        <div className="seg" role="tablist">
          {["1H", "24H", "7D", "30D", "90D"].map((r) => (
            <button key={r} className={"seg__btn" + (r === "30D" ? " active" : "")}>{r}</button>
          ))}
        </div>
        <div style={{ width: 1, height: 20, background: "var(--border)" }}></div>
        <button className="chip"><Icon.Filter size={12} /> All projects</button>
        <button className="chip chip--applied"><Icon.Database size={12} /> 2 sources</button>
        <button className="chip"><Icon.Users size={12} /> All users</button>
        <div className="spacer"></div>
        <span className="mono subtle" style={{ fontSize: 11.5, paddingRight: 6 }}>Apr 1 → Apr 29 · UTC</span>
        <button className="icon-btn" title="Calendar"><Icon.Calendar size={15} /></button>
      </div>

      {/* KPI grid */}
      <div className="kpi-grid">
        <KPI
          dot="brand" label="Active subscriptions"
          value="124" delta="+8" deltaDir="up"
          sub="across 12 sources"
          spark={<Sparkline points={sparkA} color="var(--brand-500)" />}
        />
        <KPI
          dot="info" label="Query executions"
          value="1,284" delta="+12.4%" deltaDir="up"
          sub="38 queries configured"
          spark={<Sparkline points={sparkB} color="oklch(60% 0.13 240)" />}
        />
        <KPI
          dot="warn" label="Notifications sent"
          value="47" delta="+3" deltaDir="up"
          sub="4 channels · 12 recipients"
          spark={<Sparkline points={sparkC} color="oklch(70% 0.15 70)" />}
        />
        <KPI
          dot="crit" label="Anomalies detected"
          value="6" delta="-2" deltaDir="down"
          sub={<><span className="pill pill--crit" style={{ marginRight: 6 }}>2 open</span> 4 acknowledged</>}
          spark={<Sparkline points={sparkD} color="oklch(60% 0.19 25)" />}
          intent="crit"
        />
      </div>

      {/* Performance KPIs */}
      <div className="row row--3-up">
        <PerfKPI dot="ok" label="Avg execution" value="142" unit="ms" sub="across all queries" delta="-8%" deltaDir="down" />
        <PerfKPI dot="ok" label="Fastest query" value="14" unit="ms" sub={<>top: <span className="mono">orders.daily_revenue</span></>} delta="-2 ms" deltaDir="down" />
        <PerfKPI dot="warn" label="Slowest query" value="2,140" unit="ms" sub={<>review: <span className="mono">events.session_join</span></>} delta="+340 ms" deltaDir="up" />
      </div>

      {/* Activity trends + system overview */}
      <div className="row row--7-3">
        <div className="card">
          <div className="card__head">
            <Icon.Trend size={15} className="muted" />
            <h3 className="card__title">Activity trends</h3>
            <span className="card__sub">last 30 days</span>
            <div className="card__actions">
              <div className="legend">
                <span className="legend__item"><span className="legend__sw" style={{ background: "var(--brand-500)" }}></span>Queries executed</span>
                <span className="legend__item"><span className="legend__sw" style={{ background: "oklch(70% 0.15 70)" }}></span>Notifications sent</span>
              </div>
              <div className="divider-v"></div>
              <button className="icon-btn" title="More"><Icon.Dots size={15} /></button>
            </div>
          </div>
          <div className="chart-wrap">
            <LineChart
              days={30}
              series={[
                { name: "Queries", color: "var(--brand-500)", data: trendQueries },
                { name: "Notifs", color: "oklch(70% 0.15 70)", data: trendNotifs },
              ]}
            />
          </div>
        </div>

        <div className="card">
          <div className="card__head">
            <Icon.Layers size={15} className="muted" />
            <h3 className="card__title">System overview</h3>
            <div className="card__actions">
              <span className="pill pill--ok"><span style={{ width: 6, height: 6, borderRadius: 99, background: "var(--ok)" }}></span> Healthy</span>
            </div>
          </div>
          <div className="stat-list">
            <StatRow icon="Database" label="Data sources" value="12" trail={<span className="pill pill--ok">12 online</span>} />
            <StatRow icon="Users" label="Recipients" value="48" trail={<span className="mono subtle">8 teams</span>} />
            <StatRow icon="Check" label="Unresolved tasks" value="7" trail={<span className="pill pill--warn">3 overdue</span>} />
            <StatRow icon="Alert" label="Active anomalies" value="2" trail={<span className="pill pill--crit">action req.</span>} />
            <StatRow icon="Plug" label="Integrations" value="9" trail={<span className="mono subtle">+ webhook</span>} />
          </div>
        </div>
      </div>

      {/* Query exec performance */}
      <div className="card">
        <div className="card__head">
          <Icon.Bolt size={15} className="muted" />
          <h3 className="card__title">Query execution performance</h3>
          <span className="card__sub">last 30 days · all sources</span>
          <div className="card__actions">
            <div className="seg">
              <button className="seg__btn active">avg</button>
              <button className="seg__btn">p50</button>
              <button className="seg__btn">p95</button>
              <button className="seg__btn">p99</button>
            </div>
            <button className="icon-btn"><Icon.Dots size={15} /></button>
          </div>
        </div>
        <div className="chart-wrap">
          <PerfHistogram buckets={buckets} />
        </div>
        <div style={{ display: "flex", gap: 16, padding: "0 16px 14px", alignItems: "center", flexWrap: "wrap" }}>
          <span className="legend__item legend"><span className="legend__sw" style={{ background: "var(--brand-500)" }}></span>typical</span>
          <span className="legend__item legend"><span className="legend__sw" style={{ background: "var(--warn)" }}></span>p95 spike</span>
          <span className="legend__item legend"><span className="legend__sw" style={{ background: "var(--crit)" }}></span>p99 spike</span>
          <div className="spacer"></div>
          <span className="mono subtle" style={{ fontSize: 12 }}>2 outliers detected on Apr 22 · <a href="#" style={{ color: "var(--brand-600)" }}>investigate →</a></span>
        </div>
      </div>

      {/* Bottom: migration + tasks + activity */}
      <div className="row row--7-3">
        <div className="row" style={{ gridTemplateRows: "auto auto", gap: 16 }}>
          <div className="card">
            <div className="card__head">
              <Icon.ArrowsLR size={15} className="muted" />
              <h3 className="card__title">Data migration</h3>
              <span className="card__sub">overview</span>
              <div className="card__actions">
                <a href="#" style={{ fontSize: 12.5, color: "var(--brand-600)", textDecoration: "none", fontWeight: 500 }}>Open jobs →</a>
              </div>
            </div>
            <div className="card__body card__body--flush">
              <div className="mini-grid">
                <Mini dot="var(--info)" label="Total jobs" value="14" bar="100%" barColor="var(--info)" />
                <Mini dot="var(--ok)" label="Successful" value="11" bar="78%" barColor="var(--ok)" />
                <Mini dot="var(--brand-500)" label="Executions" value="284" bar="62%" barColor="var(--brand-500)" />
                <Mini dot="var(--crit)" label="Errored" value="2" bar="14%" barColor="var(--crit)" />
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card__head">
              <Icon.Check size={15} className="muted" />
              <h3 className="card__title">Task management</h3>
              <span className="card__sub">7 unresolved</span>
              <div className="card__actions">
                <div className="seg">
                  <button className="seg__btn active">Mine</button>
                  <button className="seg__btn">Team</button>
                  <button className="seg__btn">All</button>
                </div>
              </div>
            </div>
            <div className="card__body card__body--flush">
              <div className="mini-grid mini-grid--3">
                <Mini dot="var(--text-muted)" label="Total" value="34" bar="100%" barColor="var(--text-muted)" />
                <Mini dot="var(--warn)" label="Open" value="7" bar="22%" barColor="var(--warn)" />
                <Mini dot="var(--ok)" label="Resolved" value="27" bar="79%" barColor="var(--ok)" />
              </div>
            </div>
          </div>
        </div>

        <div className="card">
          <div className="card__head">
            <Icon.Activity size={15} className="muted" />
            <h3 className="card__title">Recent activity</h3>
            <div className="card__actions">
              <a href="#" style={{ fontSize: 12.5, color: "var(--brand-600)", textDecoration: "none", fontWeight: 500 }}>View all →</a>
            </div>
          </div>
          <div className="feed">
            <FeedItem
              tone="ok"
              icon="Check"
              title={<><b>orders.daily_revenue</b> ran successfully</>}
              meta="trigger · scheduled · 142ms"
              time="2m"
            />
            <FeedItem
              tone="warn"
              icon="Alert"
              title={<>Anomaly: <b>signups.eu</b> down 38% vs 7d</>}
              meta="threshold breached · severity warn"
              time="14m"
            />
            <FeedItem
              tone="info"
              icon="Plus"
              title={<><b>mirko</b> created subscription <span className="mono">weekly_kpi_brief</span></>}
              meta="3 recipients"
              time="1h"
            />
            <FeedItem
              tone="brand"
              icon="Bot"
              title={<>AI Actor <b>ledger-watcher</b> resolved task #214</>}
              meta="auto-assigned to mirko"
              time="3h"
            />
            <FeedItem
              tone="crit"
              icon="Alert"
              title={<><b>postgres-eu</b> slow query > 2s</>}
              meta="events.session_join"
              time="5h"
            />
          </div>
        </div>
      </div>

      {/* Footer info */}
      <div className="row-spread" style={{ paddingTop: 4 }}>
        <span className="mono subtle" style={{ fontSize: 11.5 }}>
          Beacon v0.93 · region eu-west · build 2026.04.29-r3
        </span>
        <span className="mono subtle" style={{ fontSize: 11.5 }}>
          Press <span className="kbd">⌘</span> <span className="kbd">K</span> to search · <span className="kbd">G</span> then <span className="kbd">H</span> for home
        </span>
      </div>
    </div>
  );
}

function KPI({ dot = "brand", label, value, sub, delta, deltaDir, spark, intent }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={"kpi__dot kpi__dot--" + dot}></span>
        <span className="kpi__label">{label}</span>
        <button className="kpi__menu"><Icon.Dots size={14} /></button>
      </div>
      <div className="kpi__value">{value}</div>
      <div className="kpi__sub">
        {delta && (
          <span className={"kpi__delta" + (deltaDir === "down" ? (intent === "crit" ? "" : " kpi__delta--down") : "")}>
            {deltaDir === "up" ? <Icon.ArrowUp size={11} /> : deltaDir === "down" ? <Icon.ArrowDown size={11} /> : null}
            {delta}
          </span>
        )}
        <span>{sub}</span>
      </div>
      {spark}
    </div>
  );
}

function PerfKPI({ dot, label, value, unit, sub, delta, deltaDir }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className={"kpi__dot kpi__dot--" + dot}></span>
        <span className="kpi__label">{label}</span>
        <button className="kpi__menu"><Icon.Dots size={14} /></button>
      </div>
      <div className="kpi__value">{value}<span className="kpi__unit">{unit}</span></div>
      <div className="kpi__sub">
        {delta && (
          <span className={"kpi__delta" + (deltaDir === "down" ? "" : " kpi__delta--down")}>
            {deltaDir === "up" ? <Icon.ArrowUp size={11} /> : <Icon.ArrowDown size={11} />}
            {delta}
          </span>
        )}
        <span>{sub}</span>
      </div>
    </div>
  );
}

function StatRow({ icon, label, value, trail }) {
  const IconCmp = Icon[icon] || Icon.Box;
  return (
    <div className="stat-row">
      <div className="stat-row__icon"><IconCmp size={15} /></div>
      <div className="stat-row__main">
        <div className="stat-row__label">{label}</div>
        <div className="stat-row__value">{value}</div>
      </div>
      {trail}
      <Icon.Chevron size={14} className="stat-row__chevron" />
    </div>
  );
}

function Mini({ dot, label, value, bar, barColor }) {
  return (
    <div className="mini">
      <div className="mini__label">
        <span className="mini__dot" style={{ background: dot }}></span>
        {label}
      </div>
      <div className="mini__value">{value}</div>
      <div className="mini__bar"><span style={{ width: bar, background: barColor }}></span></div>
    </div>
  );
}

function FeedItem({ tone = "info", icon = "Info", title, meta, time }) {
  const IconCmp = Icon[icon] || Icon.Info;
  const colors = {
    ok: { bg: "var(--ok-bg)", c: "var(--ok)" },
    warn: { bg: "var(--warn-bg)", c: "var(--warn)" },
    crit: { bg: "var(--crit-bg)", c: "var(--crit)" },
    info: { bg: "var(--info-bg)", c: "var(--info)" },
    brand: { bg: "var(--brand-50)", c: "var(--brand-600)" },
  }[tone];
  return (
    <div className="feed__item">
      <div className="feed__icon" style={{ background: colors.bg, color: colors.c }}>
        <IconCmp size={12} />
      </div>
      <div className="feed__main">
        <div className="feed__title">{title}</div>
        <div className="feed__meta">{meta}</div>
      </div>
      <div className="feed__time">{time}</div>
    </div>
  );
}

window.Dashboard = Dashboard;
window.KPI = KPI;
window.PerfKPI = PerfKPI;
window.StatRow = StatRow;
window.Mini = Mini;
window.FeedItem = FeedItem;

// ===== Beacon hero header =====
function BeaconHeader() {
  const [now, setNow] = React.useState(() => new Date());
  const [tickedSec, setTickedSec] = React.useState(12);

  React.useEffect(() => {
    const t = setInterval(() => {
      setNow(new Date());
      setTickedSec((s) => (s >= 60 ? 1 : s + 1));
    }, 1000);
    return () => clearInterval(t);
  }, []);

  const hour = now.getHours();
  const greet =
    hour < 5 ? "Late shift"
    : hour < 12 ? "Good morning"
    : hour < 17 ? "Good afternoon"
    : hour < 21 ? "Good evening"
    : "Late shift";

  const ts = now.toLocaleTimeString("en-GB", { hour12: false });
  const date = now.toLocaleDateString("en-GB", { weekday: "short", day: "2-digit", month: "short" }).toUpperCase();

  // Mini status rail — last 24 ticks
  const ticks = React.useMemo(() => {
    const arr = [];
    for (let i = 0; i < 24; i++) {
      const r = Math.random();
      arr.push(r < 0.04 ? "crit" : r < 0.12 ? "warn" : "ok");
    }
    return arr;
  }, []);

  return (
    <div className="beacon-hero">
      <div className="beacon-hero__sweep" aria-hidden="true">
        <svg viewBox="0 0 600 200" preserveAspectRatio="none" width="100%" height="100%">
          <defs>
            <radialGradient id="bh-radial" cx="0%" cy="50%" r="60%">
              <stop offset="0%" stopColor="var(--brand-500)" stopOpacity="0.35" />
              <stop offset="55%" stopColor="var(--brand-500)" stopOpacity="0.08" />
              <stop offset="100%" stopColor="var(--brand-500)" stopOpacity="0" />
            </radialGradient>
            <pattern id="bh-grid" width="24" height="24" patternUnits="userSpaceOnUse">
              <path d="M24 0H0V24" fill="none" stroke="currentColor" strokeWidth="0.5" opacity="0.6" />
            </pattern>
          </defs>
          <rect width="600" height="200" fill="url(#bh-grid)" className="beacon-hero__grid" />
          <rect width="600" height="200" fill="url(#bh-radial)" />
          <g className="beacon-hero__rings">
            <circle cx="0" cy="100" r="60" fill="none" stroke="var(--brand-500)" strokeOpacity="0.18" />
            <circle cx="0" cy="100" r="120" fill="none" stroke="var(--brand-500)" strokeOpacity="0.13" />
            <circle cx="0" cy="100" r="180" fill="none" stroke="var(--brand-500)" strokeOpacity="0.08" />
          </g>
        </svg>
        <div className="beacon-hero__beam"></div>
      </div>

      <div className="beacon-hero__inner">
        <div className="beacon-hero__left">
          <div className="beacon-hero__eyebrow">
            <span className="mono">{date}</span>
            <span className="beacon-hero__sep">·</span>
            <span className="mono">{ts} UTC</span>
            <span className="beacon-hero__sep">·</span>
            <span className="live-pip"><span className="live-pip__dot"></span>BEACON ACTIVE</span>
          </div>
          <h1 className="beacon-hero__title">
            <span className="beacon-hero__greet">{greet}, mirko.</span>
            <span className="beacon-hero__headline">
              <span>Everything is</span>
              <span className="beacon-hero__word">
                nominal
                <svg className="beacon-hero__underline" viewBox="0 0 220 14" preserveAspectRatio="none" aria-hidden="true">
                  <path d="M2 9 Q 40 2, 80 7 T 160 7 T 218 5" fill="none" stroke="var(--brand-500)" strokeWidth="2.5" strokeLinecap="round" />
                </svg>
              </span>
            </span>
          </h1>
          <p className="beacon-hero__sub">
            <span>1,284 queries executed in the last 30 days · </span>
            <span className="mono">2 anomalies </span>
            <span>require attention.</span>
          </p>
        </div>

        <div className="beacon-hero__right">
          <div className="beacon-hero__rail" title="Last 24 hours of system status">
            <div className="beacon-hero__rail-label">
              <span>SYSTEM · LAST 24H</span>
              <span className="mono">{tickedSec.toString().padStart(2, "0")}s</span>
            </div>
            <div className="beacon-hero__rail-bars">
              {ticks.map((t, i) => (
                <span key={i} className={"beacon-hero__tick beacon-hero__tick--" + t} style={{ animationDelay: (i * 30) + "ms" }}></span>
              ))}
            </div>
            <div className="beacon-hero__rail-axis">
              <span className="mono subtle">−24h</span>
              <span className="mono subtle">now</span>
            </div>
          </div>
          <div className="beacon-hero__actions">
            <button className="btn btn--ghost"><Icon.Download className="btn__icon" /> Export</button>
            <button className="btn"><Icon.Refresh className="btn__icon" /> Refresh</button>
            <button className="btn btn--primary"><Icon.Plus className="btn__icon" /> New query</button>
          </div>
        </div>
      </div>
    </div>
  );
}

window.BeaconHeader = BeaconHeader;
