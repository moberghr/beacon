import type { APIRoute } from 'astro';
import { bundle, docsBySlugs, orderedDocs } from '../lib/bundles';

// The integration checklist's MCP step lives in the MCP section; it belongs after the AI providers page.
async function withMcpStep() {
	const pages = await orderedDocs(['Integrate Beacon']);
	const at = pages.findIndex((x) => x.id === 'integrate/ai') + 1;
	return [...pages.slice(0, at), ...(await docsBySlugs(['mcp/connect'])), ...pages.slice(at)];
}

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon: integration guide',
			'Everything needed to embed Beacon in an ASP.NET Core application, in the order you should do it. Start with the integration overview: it is a step-by-step checklist; the pages after it give every option for each step.',
			await withMcpStep(),
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
