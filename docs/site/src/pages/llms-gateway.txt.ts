import type { APIRoute } from 'astro';
import { bundle, docsBySlugs } from '../lib/bundles';

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon: MCP gateway guide',
			'Everything needed to give AI clients governed access to your own ASP.NET Core application with Beacon: your users sign in with Entra, your endpoints become tools that run as them, your docs become grounding, no data sources. Start with the first page: it is the whole recipe, step by step; the pages after it give every option of the parts it uses.',
			await docsBySlugs(['mcp/gateway', 'mcp/host-endpoint-tools', 'cookbooks/endpoint-as-tool', 'mcp/host-docs', 'integrate/troubleshooting']),
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
