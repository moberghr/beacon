import type { APIRoute } from 'astro';
import { bundle, orderedDocs } from '../lib/bundles';

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon: MCP server',
			"Everything about Beacon's Model Context Protocol server in one file: the tools and their guardrails, connecting AI clients with API keys or Microsoft Entra, the gateway mode for your own application, saved-query and endpoint tools, grounding, and the audit. Start with the MCP overview: it says which page answers which question.",
			await orderedDocs(['MCP', 'Extend MCP']),
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
