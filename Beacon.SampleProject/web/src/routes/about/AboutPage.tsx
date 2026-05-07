import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';

interface Capability {
  icon: keyof typeof Icon;
  title: string;
  body: string;
}

const CAPABILITIES: Capability[] = [
  {
    icon: 'Search',
    title: 'Query monitoring',
    body:
      'Author semantic SQL queries that monitor data quality, business rules, and database health. Multi-step execution with parameter substitution.',
  },
  {
    icon: 'Bell',
    title: 'Smart alerting',
    body:
      'Deliver notifications via Email, Microsoft Teams, Slack, or Jira with rich formatting and full result attachments.',
  },
  {
    icon: 'ArrowsLR',
    title: 'Data migration',
    body:
      'Orchestrate ETL workflows with Insert, Upsert, Truncate, and Sync modes across heterogeneous database engines.',
  },
  {
    icon: 'Database',
    title: 'Cross-database joins',
    body:
      'Query PostgreSQL, SQL Server, and MySQL simultaneously with virtual-table abstractions backed by in-memory SQLite.',
  },
  {
    icon: 'Check',
    title: 'Task management',
    body:
      'Subscriptions auto-create tasks, track lifecycle, auto-resolve when issues clear, and support comments and collaboration.',
  },
  {
    icon: 'Bot',
    title: 'AI-assisted features',
    body:
      'Documentation generation, natural-language alert authoring, and intelligent insights via swappable LLM providers.',
  },
  {
    icon: 'Trend',
    title: 'Anomaly detection',
    body:
      'Statistical baselines (Z-score, IQR, percent change) with configurable thresholds and real-time anomaly alerts.',
  },
  {
    icon: 'Tower',
    title: 'Control Tower',
    body:
      'Real-time RAG health view across every subscription and data source, with one-click drill-in to the offending query.',
  },
];

const USE_CASES = [
  { title: 'Data quality monitoring', body: 'Alert on orphaned records, invalid states, NULL violations, and constraint failures.' },
  { title: 'Automated reporting', body: 'Schedule daily, weekly, and monthly reports delivered as Excel or CSV attachments.' },
  { title: 'Database health', body: 'Monitor table growth, connection counts, disk usage, and replication lag with proactive alerts.' },
  { title: 'Cross-database integration', body: 'ETL from production to warehouse, sync master data across systems, build consolidated metrics.' },
  { title: 'Business rule enforcement', body: 'Track SLA violations, inventory thresholds, payment delays, and compliance with automatic Jira tickets.' },
  { title: 'Incident management', body: 'Open Jira issues on first detection, comment on follow-ups, auto-close when resolved.' },
];

const STACK = [
  { label: 'Framework', items: ['.NET 9', 'C# 13', 'ASP.NET Core 9'] },
  { label: 'UI', items: ['React 18', 'Vite', 'TypeScript', 'Beacon design system'] },
  { label: 'Data access', items: ['EF Core 9', 'Dapper', 'EFCore.BulkExtensions'] },
  { label: 'Databases', items: ['Npgsql', 'Microsoft.Data.SqlClient', 'MySql.Data'] },
  { label: 'Background jobs', items: ['Hangfire', 'Cronos'] },
  { label: 'Integrations', items: ['Atlassian.SDK', 'AdaptiveCards', 'ClosedXML', 'CsvHelper'] },
];

export default function AboutPage() {
  return (
    <div className="page">
      <PageHeader
        title="About Beacon"
        sub={<span className="muted">Semantic database monitoring and orchestration</span>}
      />

      <div className="card">
        <div className="card__body">
          <h2 style={{ margin: 0, color: 'var(--text)' }}>Semantic monitoring, smart alerts, cross-database orchestration.</h2>
          <p className="muted" style={{ marginTop: 8, lineHeight: 1.55 }}>
            Beacon turns SQL queries into a monitoring platform. Author semantic checks, schedule them
            with cron, deliver alerts to the channels your team actually uses, and orchestrate ETL
            workflows across PostgreSQL, SQL Server, and MySQL — all from one place.
          </p>
        </div>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div className="card__head">
          <Icon.Lightbulb size={14} className="muted" />
          <h3 className="card__title">Core capabilities</h3>
        </div>
        <div className="card__body">
          <div style={{ display: 'grid', gap: 16, gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' }}>
            {CAPABILITIES.map(c => {
              const I = Icon[c.icon];
              return (
                <div key={c.title} style={{ padding: 12, border: '1px solid var(--border)', borderRadius: 8 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 6 }}>
                    <I size={16} />
                    <strong style={{ color: 'var(--text)' }}>{c.title}</strong>
                  </div>
                  <div className="muted" style={{ fontSize: 13, lineHeight: 1.5 }}>{c.body}</div>
                </div>
              );
            })}
          </div>
        </div>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div className="card__head">
          <Icon.Layers size={14} className="muted" />
          <h3 className="card__title">Common use cases</h3>
        </div>
        <div className="card__body">
          <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))' }}>
            {USE_CASES.map(u => (
              <div key={u.title} style={{ padding: 12, borderLeft: '3px solid var(--brand-500)', background: 'var(--surface-2)' }}>
                <div style={{ fontWeight: 600, color: 'var(--text)' }}>{u.title}</div>
                <div className="muted" style={{ fontSize: 13, marginTop: 4, lineHeight: 1.5 }}>{u.body}</div>
              </div>
            ))}
          </div>
        </div>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div className="card__head">
          <Icon.Cog size={14} className="muted" />
          <h3 className="card__title">Technology stack</h3>
        </div>
        <div className="card__body">
          <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))' }}>
            {STACK.map(s => (
              <div key={s.label}>
                <div style={{ fontSize: 12, color: 'var(--text-muted)', textTransform: 'uppercase', letterSpacing: 0.4 }}>{s.label}</div>
                <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginTop: 6 }}>
                  {s.items.map(item => <span key={item} className="pill">{item}</span>)}
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>

      <div className="muted" style={{ textAlign: 'center', marginTop: 24, fontSize: 12 }}>
        Beacon · Mirko Budimir, Moberg
      </div>
    </div>
  );
}
