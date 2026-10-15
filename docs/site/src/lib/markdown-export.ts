// Turns a docs entry into standalone Markdown for AI assistants: absolute links,
// a title header, and no MDX import lines.
import type { CollectionEntry } from 'astro:content';

export const SITE_ORIGIN = 'https://moberghr.github.io';
export const BASE = '/beacon';
export const SITE_URL = `${SITE_ORIGIN}${BASE}`;

export function pageUrl(id: string): string {
	return id === 'index' ? `${SITE_URL}/` : `${SITE_URL}/${id.replace(/\/$/, '')}/`;
}

export function absolutise(markdown: string): string {
	// ](/features/x/) and src="/img/..." → absolute URLs on the published site.
	return markdown
		.replace(/\]\((\/(?!\/)[^)\s]*)\)/g, (_, path: string) => `](${SITE_URL}${path})`)
		.replace(/(href|src)="(\/(?!\/)[^"]*)"/g, (_, attr: string, path: string) => `${attr}="${SITE_URL}${path}"`);
}

export function toMarkdown(entry: CollectionEntry<'docs'>): string {
	const body = (entry.body ?? '')
		.split('\n')
		.filter((line) => !/^import\s.+from\s.+;?\s*$/.test(line))
		.join('\n')
		.trim();
	const description = entry.data.description ? `\n\n> ${entry.data.description}` : '';
	return `# ${entry.data.title}${description}\n\nSource: ${pageUrl(entry.id)}\n\n${absolutise(body)}\n`;
}
