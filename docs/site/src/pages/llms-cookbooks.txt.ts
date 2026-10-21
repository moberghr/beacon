import type { APIRoute } from 'astro';
import { bundle, docsBySlugs, orderedDocs } from '../lib/bundles';

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon: cookbooks',
			'Every Beacon recipe in one file, each one start to finish with a check that it works, plus the prompt library. Pick the recipe that matches the goal from the list on the first page, follow it in order, and ask before every decision a recipe marks as one.',
			[
				...(await orderedDocs(['Cookbooks', 'Connect AI to your data', 'Make your app AI-ready', 'Embed Beacon', 'Monitor & govern'])),
				...(await docsBySlugs(['mcp/gateway'])),
			],
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
