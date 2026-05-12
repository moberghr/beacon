import {
  Search,
  Bell,
  ArrowLeftRight,
  Database,
  Check,
  Bot,
  TrendingUp,
  Radio,
  Lightbulb,
  Layers,
  Settings,
  type LucideIcon,
} from 'lucide-react';
import { Card, CardHead, CardTitle, CardBody, PageHeader, Pill } from '@/components/beacon';

interface Capability {
  Icon: LucideIcon;
  title: string;
  body: string;
}

const CAPABILITIES: Capability[] = [
  {
    Icon: Search,
    title: 'Query monitoring',
    body:
      'Author semantic SQL queries that monitor data quality, business rules, and database health. Multi-step execution with parameter substitution.',
  },
  {
    Icon: Bell,
    title: 'Smart alerting',
    body:
      'Deliver notifications via Email, Microsoft Teams, Slack, or Jira with rich formatting and full result attachments.',
  },
  {
    Icon: ArrowLeftRight,
    title: 'Data migration',
    body:
      'Orchestrate ETL workflows with Insert, Upsert, Truncate, and Sync modes across heterogeneous database engines.',
  },
  {
    Icon: Database,
    title: 'Cross-database joins',
    body:
      'Query PostgreSQL, SQL Server, and MySQL simultaneously with virtual-table abstractions backed by in-memory SQLite.',
  },
  {
    Icon: Check,
    title: 'Task management',
    body:
      'Subscriptions auto-create tasks, track lifecycle, auto-resolve when issues clear, and support comments and collaboration.',
  },
  {
    Icon: Bot,
    title: 'AI-assisted features',
    body:
      'Documentation generation, natural-language alert authoring, and intelligent insights via swappable LLM providers.',
  },
  {
    Icon: TrendingUp,
    title: 'Anomaly detection',
    body:
      'Statistical baselines (Z-score, IQR, percent change) with configurable thresholds and real-time anomaly alerts.',
  },
  {
    Icon: Radio,
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
    <div className="flex flex-col gap-4 p-7">
      <PageHeader
        eyebrow="Platform"
        prefix="About"
        emphasis="Beacon"
        sub={<span className="text-text-muted">Semantic database monitoring and orchestration</span>}
      />

      <Card>
        <CardBody>
          <h2 className="m-0 text-text">Semantic monitoring, smart alerts, cross-database orchestration.</h2>
          <p className="text-text-muted mt-2 leading-relaxed m-0">
            Beacon turns SQL queries into a monitoring platform. Author semantic checks, schedule them
            with cron, deliver alerts to the channels your team actually uses, and orchestrate ETL
            workflows across PostgreSQL, SQL Server, and MySQL — all from one place.
          </p>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Lightbulb size={14} className="text-text-muted" />
          <CardTitle>Core capabilities</CardTitle>
        </CardHead>
        <CardBody>
          <div className="grid gap-4 grid-cols-[repeat(auto-fill,minmax(260px,1fr))]">
            {CAPABILITIES.map(c => (
              <div key={c.title} className="p-3 border border-border rounded-md">
                <div className="flex items-center gap-2 mb-1.5">
                  <c.Icon size={16} />
                  <strong className="text-text">{c.title}</strong>
                </div>
                <div className="text-text-muted text-sm leading-relaxed">{c.body}</div>
              </div>
            ))}
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Layers size={14} className="text-text-muted" />
          <CardTitle>Common use cases</CardTitle>
        </CardHead>
        <CardBody>
          <div className="grid gap-3 grid-cols-[repeat(auto-fill,minmax(260px,1fr))]">
            {USE_CASES.map(u => (
              <div key={u.title} className="p-3 border-l-[3px] border-brand-500 bg-surface-2">
                <div className="font-semibold text-text">{u.title}</div>
                <div className="text-text-muted text-sm mt-1 leading-relaxed">{u.body}</div>
              </div>
            ))}
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHead>
          <Settings size={14} className="text-text-muted" />
          <CardTitle>Technology stack</CardTitle>
        </CardHead>
        <CardBody>
          <div className="grid gap-3 grid-cols-[repeat(auto-fill,minmax(220px,1fr))]">
            {STACK.map(s => (
              <div key={s.label}>
                <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">{s.label}</div>
                <div className="flex gap-1.5 flex-wrap mt-1.5">
                  {s.items.map(item => <Pill key={item}>{item}</Pill>)}
                </div>
              </div>
            ))}
          </div>
        </CardBody>
      </Card>

      <div className="text-text-muted text-center mt-6 text-xs">
        Beacon · Mirko Budimir, Moberg
      </div>
    </div>
  );
}
