// Builds the llms.txt family from the shared sidebar so the bundles follow the site's order.
import { getCollection, type CollectionEntry } from 'astro:content';
import { SECTIONS } from '../sidebar.mjs';
import { SITE_URL, pageUrl, toMarkdown } from './markdown-export';

type Doc = CollectionEntry<'docs'>;

export async function docsById(): Promise<Map<string, Doc>> {
	const docs = await getCollection('docs');
	return new Map(docs.map((entry) => [entry.id, entry]));
}

/** Entries of the given sections, in sidebar order, each page once. */
export async function orderedDocs(sectionLabels?: string[]): Promise<Doc[]> {
	const byId = await docsById();
	const seen = new Set<string>();
	const result: Doc[] = [];
	for (const section of SECTIONS) {
		if (section.agents === false || (sectionLabels && !sectionLabels.includes(section.label))) {
			continue;
		}
		for (const item of section.items) {
			const entry = byId.get(item.slug);
			if (entry && !seen.has(entry.id)) {
				seen.add(entry.id);
				result.push(entry);
			}
		}
	}
	return result;
}

export function bundle(title: string, intro: string, docs: Doc[]): string {
	const header = `# ${title}\n\n> ${intro}\n\nPublished at ${SITE_URL}/. Every page is also available as Markdown at <page-url>.md.\n`;
	return [header, ...docs.map(toMarkdown)].join('\n\n---\n\n');
}

export async function llmsIndex(): Promise<string> {
	const byId = await docsById();
	const lines = [
		'# Beacon',
		'',
		'> Beacon is a .NET 10 platform for database monitoring, alerting and data movement, with a governed Model Context Protocol (MCP) server that gives AI assistants read-only, project-scoped, masked and audited access to your databases. It runs standalone or is embedded in an ASP.NET Core application through the Moberg.Beacon.* NuGet packages.',
		'',
		'To integrate Beacon into an application, read the integration bundle first:',
		'',
		`- [Integration guide (all integration pages, one file)](${SITE_URL}/llms-integrate.txt): everything needed to embed Beacon in an ASP.NET Core host`,
		`- [Full documentation (one file)](${SITE_URL}/llms-full.txt): every page of these docs`,
		'',
	];
	for (const section of SECTIONS.filter((x) => x.agents !== false)) {
		lines.push(`## ${section.label}`, '', section.summary, '');
		for (const item of section.items) {
			const entry = byId.get(item.slug);
			if (!entry) {
				continue;
			}
			const description = entry.data.description ? `: ${entry.data.description}` : '';
			lines.push(`- [${entry.data.title}](${pageUrl(entry.id).replace(/\/$/, '')}.md)${description}`);
		}
		lines.push('');
	}
	lines.push('## Optional', '', '- [Source code](https://github.com/moberghr/beacon): the reference host is src/Beacon.SampleProject/Program.cs', '');
	return lines.join('\n');
}
