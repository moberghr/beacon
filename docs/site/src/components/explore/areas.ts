// The Explore pages, in reading order. Used by the area navigation, the footer and the landing cards.
export interface Area {
	slug: string;
	label: string;
	tagline: string;
}

export const AREAS: Area[] = [
	{ slug: 'monitoring', label: 'Monitoring', tagline: 'Scheduled SQL checks across every engine' },
	{ slug: 'alerting', label: 'Alerting & tasks', tagline: 'The right message to the right channel' },
	{ slug: 'data-quality', label: 'Data quality', tagline: 'Contracts that score your tables' },
	{ slug: 'ai-and-mcp', label: 'AI & MCP', tagline: 'Governed answers for AI assistants' },
	{ slug: 'grounding', label: 'Grounding & learning', tagline: 'Context that makes SQL generation accurate' },
	{ slug: 'governance', label: 'Security & governance', tagline: 'Access, approvals and audit' },
	{ slug: 'data-movement', label: 'Data movement', tagline: 'Reviewed jobs that copy data between engines' },
	{ slug: 'extending', label: 'Extending your app', tagline: 'Your model, docs and endpoints in Beacon' },
];
