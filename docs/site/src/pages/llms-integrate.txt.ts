import type { APIRoute } from 'astro';
import { bundle, orderedDocs } from '../lib/bundles';

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon — integration guide',
			'Everything needed to embed Beacon in an ASP.NET Core application, in the order you should do it. Start with the integration overview: it is a step-by-step checklist; the pages after it give every option for each step.',
			await orderedDocs(['Integrate Beacon']),
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
