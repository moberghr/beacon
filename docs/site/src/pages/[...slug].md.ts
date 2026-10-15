// Every doc page as raw Markdown at <page>.md, for "Copy as Markdown" and for agents.
import type { APIRoute, GetStaticPaths } from 'astro';
import { getCollection, type CollectionEntry } from 'astro:content';
import { toMarkdown } from '../lib/markdown-export';

export const getStaticPaths: GetStaticPaths = async () => {
	const docs = await getCollection('docs');
	return docs
		.filter((entry) => entry.data.template !== 'splash')
		.map((entry) => ({ params: { slug: entry.id }, props: { entry } }));
};

export const GET: APIRoute = ({ props }) =>
	new Response(toMarkdown(props.entry as CollectionEntry<'docs'>), {
		headers: { 'Content-Type': 'text/markdown; charset=utf-8' },
	});
