import type { APIRoute } from 'astro';
import { bundle, orderedDocs } from '../lib/bundles';

export const GET: APIRoute = async () =>
	new Response(
		bundle(
			'Beacon — full documentation',
			'Every page of the Beacon documentation in one file, in navigation order.',
			await orderedDocs(),
		),
		{ headers: { 'Content-Type': 'text/plain; charset=utf-8' } },
	);
