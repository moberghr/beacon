// The docs navigation. Shared by astro.config.mjs (Starlight sidebar) and the
// llms*.txt endpoints, so the AI-readable bundles follow the same order as the site.

/** @type {{ label: string, slug: string, summary: string, agents?: boolean, items: { label: string, slug: string }[] }[]} */
export const SECTIONS = [
  {
    label: 'Start here',
    slug: 'getting-started',
    summary: 'What Beacon is, what is new in 5.0, how to work with Beacon through an AI assistant, and running it locally in minutes.',
    items: [
      { label: 'Overview', slug: 'getting-started' },
      { label: "What's new in 5.0", slug: 'getting-started/whats-new' },
      { label: 'Build with AI', slug: 'getting-started/with-ai' },
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
    label: 'Cookbooks',
    slug: 'cookbooks',
    summary: 'Task-shaped recipes, each one start to finish with a prompt you can hand to your coding agent, and a library of prompts for coding agents, MCP users and admins.',
    items: [
      { label: 'All recipes', slug: 'cookbooks' },
      { label: 'Prompt library', slug: 'cookbooks/prompts' },
    ],
  },
  {
    label: 'Connect AI to your data',
    slug: 'cookbooks',
    summary: 'Recipes for connecting AI assistants to Beacon and getting good answers from them.',
    items: [
      { label: 'Connect a coding agent', slug: 'cookbooks/connect-coding-agent' },
      { label: 'Claude connector with Entra', slug: 'cookbooks/claude-connector-entra' },
      { label: 'Ask your data with an agent', slug: 'cookbooks/ask-your-data' },
      { label: 'Publish a saved query as a tool', slug: 'cookbooks/saved-query-tool' },
      { label: 'An agent for one project only', slug: 'cookbooks/agent-one-project' },
    ],
  },
  {
    label: 'Make your app AI-ready',
    slug: 'cookbooks',
    summary: 'Recipes for turning your own ASP.NET Core application into something AI assistants can use safely.',
    items: [
      { label: 'Expose an endpoint as a tool', slug: 'cookbooks/endpoint-as-tool' },
      { label: 'Ground answers in your docs', slug: 'cookbooks/ground-with-your-docs' },
    ],
  },
  {
    label: 'Embed Beacon',
    slug: 'cookbooks',
    summary: 'Recipes for running Beacon inside your own application, on the stack you already have.',
    items: [
      { label: 'PostgreSQL + Warp', slug: 'cookbooks/embed-postgresql-warp' },
      { label: 'SQL Server + Hangfire', slug: 'cookbooks/embed-sqlserver-hangfire' },
      { label: 'Single sign-on with Entra ID', slug: 'cookbooks/entra-sso' },
    ],
  },
  {
    label: 'Monitor & govern',
    slug: 'cookbooks',
    summary: 'Recipes for alerting, data quality and audit.',
    items: [
      { label: 'Alert Slack or Teams on a failed check', slug: 'cookbooks/alert-on-failed-check' },
      { label: 'Score a table with a data contract', slug: 'cookbooks/data-contract' },
      { label: 'Ship the MCP audit to your SIEM', slug: 'cookbooks/audit-to-siem' },
    ],
  },
  {
    label: 'MCP',
    slug: 'mcp',
    summary:
      "Beacon's Model Context Protocol server: the tools, connecting AI clients with API keys or Entra sign-in, the gateway mode for your own application, and the audit.",
    items: [
      { label: 'MCP overview', slug: 'mcp' },
      { label: 'Server & tools', slug: 'mcp/server' },
      { label: 'Connect AI clients', slug: 'mcp/connect' },
      { label: 'Entra ID callers', slug: 'mcp/entra-callers' },
      { label: 'Observability & audit', slug: 'mcp/observability-and-audit' },
    ],
  },
  {
    label: 'Extend MCP',
    slug: 'mcp',
    summary: 'Your own application behind MCP: the gateway mode, saved queries and endpoints as tools, your EF model and documents as grounding.',
    items: [
      { label: 'MCP gateway for your app', slug: 'mcp/gateway' },
      { label: 'Saved query tools', slug: 'mcp/saved-query-tools' },
      { label: 'Host endpoint tools', slug: 'mcp/host-endpoint-tools' },
      { label: 'Host DbContext', slug: 'mcp/host-dbcontext' },
      { label: 'Host docs', slug: 'mcp/host-docs' },
      { label: 'Grounding & knowledge base', slug: 'mcp/knowledge-base' },
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
      { label: 'Embedding saved queries', slug: 'integrate/embedding' },
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
    label: 'AI in the app',
    slug: 'features',
    summary: 'The AI layer inside Beacon: SQL assistance, documentation generation and AI actors.',
    items: [
      { label: 'AI integration', slug: 'features/ai-integration' },
      { label: 'AI actors', slug: 'features/ai-actors' },
    ],
  },
  {
    label: 'Security',
    slug: 'security',
    summary: 'The security model: projects, permissions, approvals, users, API keys, the security audit trail and admin settings.',
    items: [
      { label: 'Security model', slug: 'security' },
      { label: 'Authorization', slug: 'security/authorization' },
      { label: 'Approvals', slug: 'security/approvals' },
      { label: 'User management', slug: 'security/user-management' },
      { label: 'API keys', slug: 'security/api-keys' },
      { label: 'Security audit trail', slug: 'security/audit-trail' },
      { label: 'Admin settings', slug: 'security/admin-settings' },
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

/** Old page URLs and where they live now; astro.config.mjs turns them into redirects. */
export const MOVED = {
  'features/mcp-server': 'mcp/server',
  'features/mcp-entra-callers': 'mcp/entra-callers',
  'features/saved-query-tools': 'mcp/saved-query-tools',
  'features/host-endpoint-tools': 'mcp/host-endpoint-tools',
  'features/host-dbcontext': 'mcp/host-dbcontext',
  'features/host-docs': 'mcp/host-docs',
  'features/knowledge-base': 'mcp/knowledge-base',
  'features/mcp-observability-and-audit': 'mcp/observability-and-audit',
  'integrate/mcp': 'mcp/connect',
  'integrate/gateway': 'mcp/gateway',
  'features/security-model': 'security',
  'features/authorization': 'security/authorization',
  'features/approvals': 'security/approvals',
  'features/user-management': 'security/user-management',
  'features/api-keys': 'security/api-keys',
  'features/security-audit': 'security/audit-trail',
  'features/admin-settings': 'security/admin-settings',
};

/** Starlight sidebar built from SECTIONS. */
export const sidebar = SECTIONS.map((section) => ({
  label: section.label,
  // Every group starts closed; Starlight opens the one that holds the current page.
  collapsed: true,
  items: section.items.map((item) => ({
    label: item.label,
    link: `/${item.slug}/`,
  })),
}));
