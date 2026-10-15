// The docs navigation. Shared by astro.config.mjs (Starlight sidebar) and the
// llms*.txt endpoints, so the AI-readable bundles follow the same order as the site.

/** @type {{ label: string, slug: string, summary: string, agents?: boolean, items: { label: string, slug: string }[] }[]} */
export const SECTIONS = [
  {
    label: 'Start here',
    slug: 'getting-started',
    summary: 'What Beacon is, its core concepts, and running it locally in minutes.',
    items: [
      { label: 'Overview', slug: 'getting-started' },
      { label: 'Core concepts', slug: 'getting-started/concepts' },
      { label: 'Quick start', slug: 'getting-started/quick-start' },
      { label: 'Run the sample host', slug: 'getting-started/installation' },
    ],
  },
  {
    label: 'Explore Beacon',
    slug: 'explore',
    agents: false,
    summary: 'Showcase pages for each product area, with worked examples.',
    items: [
      { label: 'Monitoring', slug: 'explore/monitoring' },
      { label: 'Alerting & tasks', slug: 'explore/alerting' },
      { label: 'Data quality', slug: 'explore/data-quality' },
      { label: 'AI & MCP', slug: 'explore/ai-and-mcp' },
      { label: 'Grounding & learning', slug: 'explore/grounding' },
      { label: 'Security & governance', slug: 'explore/governance' },
      { label: 'Data movement', slug: 'explore/data-movement' },
      { label: 'Extending your app', slug: 'explore/extending' },
    ],
  },
  {
    label: 'Integrate Beacon',
    slug: 'integrate',
    summary:
      'Embed Beacon in your own ASP.NET Core application: packages, host wiring, database, sign-in, access control, jobs, connectors, AI, MCP and production.',
    items: [
      { label: 'Integration overview', slug: 'integrate' },
      { label: 'Packages', slug: 'integrate/packages' },
      { label: 'Host wiring', slug: 'integrate/host-setup' },
      { label: 'Metadata database', slug: 'integrate/database' },
      { label: 'Sign-in & identity', slug: 'integrate/authentication' },
      { label: 'Access control', slug: 'integrate/access-control' },
      { label: 'Background jobs', slug: 'integrate/background-jobs' },
      { label: 'Data-source connectors', slug: 'integrate/connectors' },
      { label: 'AI providers', slug: 'integrate/ai' },
      { label: 'MCP & AI clients', slug: 'integrate/mcp' },
      { label: 'Extending Beacon', slug: 'integrate/extending' },
      { label: 'Production checklist', slug: 'integrate/production' },
      { label: 'Troubleshooting', slug: 'integrate/troubleshooting' },
    ],
  },
  {
    label: 'Monitor & move data',
    slug: 'features',
    summary: 'Data sources, multi-step queries, scheduled subscriptions, alerts, tasks, anomaly detection, data quality and data migration.',
    items: [
      { label: 'Feature map', slug: 'features' },
      { label: 'Data sources', slug: 'features/data-sources' },
      { label: 'Queries', slug: 'features/queries' },
      { label: 'Subscriptions', slug: 'features/subscriptions' },
      { label: 'Notifications', slug: 'features/notifications' },
      { label: 'Control Tower', slug: 'features/control-tower' },
      { label: 'Tasks', slug: 'features/tasks' },
      { label: 'Anomaly detection', slug: 'features/anomaly-detection' },
      { label: 'Data quality', slug: 'features/data-quality' },
      { label: 'Data migration', slug: 'features/data-migration' },
    ],
  },
  {
    label: 'AI & MCP',
    slug: 'features',
    summary: 'The governed MCP server, its tools and extension points, grounding, the AI layer and AI actors.',
    items: [
      { label: 'MCP server', slug: 'features/mcp-server' },
      { label: 'Entra ID callers', slug: 'features/mcp-entra-callers' },
      { label: 'Saved query tools', slug: 'features/saved-query-tools' },
      { label: 'Host endpoint tools', slug: 'features/host-endpoint-tools' },
      { label: 'Host DbContext', slug: 'features/host-dbcontext' },
      { label: 'Host docs', slug: 'features/host-docs' },
      { label: 'Knowledge base & grounding', slug: 'features/knowledge-base' },
      { label: 'AI integration', slug: 'features/ai-integration' },
      { label: 'AI actors', slug: 'features/ai-actors' },
      { label: 'MCP observability & audit', slug: 'features/mcp-observability-and-audit' },
    ],
  },
  {
    label: 'Govern & secure',
    slug: 'features',
    summary: 'The security model: projects, permissions, approvals, users, API keys, the security audit trail and admin settings.',
    items: [
      { label: 'Security model', slug: 'features/security-model' },
      { label: 'Authorization', slug: 'features/authorization' },
      { label: 'Approvals', slug: 'features/approvals' },
      { label: 'User management', slug: 'features/user-management' },
      { label: 'API keys', slug: 'features/api-keys' },
      { label: 'Security audit trail', slug: 'features/security-audit' },
      { label: 'Admin settings', slug: 'features/admin-settings' },
    ],
  },
  {
    label: 'Reference',
    slug: 'reference',
    summary: 'Configuration keys and the URLs Beacon serves.',
    items: [
      { label: 'Configuration', slug: 'getting-started/configuration' },
      { label: 'Routes & endpoints', slug: 'reference/routes' },
    ],
  },
];

/** Starlight sidebar built from SECTIONS. */
export const sidebar = SECTIONS.map((section) => ({
  label: section.label,
  items: section.items.map((item) => ({
    label: item.label,
    link: `/${item.slug}/`,
  })),
}));
