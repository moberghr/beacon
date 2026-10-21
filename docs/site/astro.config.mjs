// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import { MOVED, sidebar } from './src/sidebar.mjs';

const BASE = '/beacon';

// Starlight base-prefixes sidebar/nav links and Astro-imported assets, but it does
// NOT prefix root-relative links/images written in Markdown body content. This tiny
// rehype plugin prefixes the base to those so content pages can use clean paths
// like /features/x/ and /img/screenshots/x.png without 404ing on the project site.
function rehypeBasePaths() {
  return (/** @type {any} */ tree) => {
    /** @param {any} node */
    const walk = (node) => {
      if (node.type === 'element' && node.properties) {
        for (const attr of ['href', 'src']) {
          const v = node.properties[attr];
          if (
            typeof v === 'string' &&
            v.startsWith('/') &&
            !v.startsWith('//') &&
            v !== BASE &&
            !v.startsWith(BASE + '/')
          ) {
            node.properties[attr] = BASE + v;
          }
        }
      }
      if (node.children) node.children.forEach(walk);
    };
    walk(tree);
  };
}

// Beacon documentation: Astro + Starlight, Moberg house brand (blue #430cda).
// Deployed to GitHub Pages at https://moberghr.github.io/beacon/
export default defineConfig({
  site: 'https://moberghr.github.io',
  base: BASE,
  trailingSlash: 'ignore',
  // Pages that moved when MCP and Security became their own sections keep their old URLs working.
  redirects: Object.fromEntries(Object.entries(MOVED).map(([from, to]) => [`/${from}`, `${BASE}/${to}/`])),
  markdown: {
    rehypePlugins: [rehypeBasePaths],
  },
  integrations: [
    // astro-mermaid must precede Starlight so its rehype step runs on doc content.
    mermaid({
      theme: 'neutral',
      autoTheme: true,
    }),
    starlight({
      title: 'Beacon',
      description:
        'Database monitoring, alerting and data movement for .NET, with a governed MCP server for AI assistants.',
      logo: {
        light: './src/assets/beacon-logo.svg',
        dark: './src/assets/beacon-logo-dark.svg',
        replacesTitle: true,
      },
      favicon: '/favicon.svg',
      customCss: ['./src/styles/moberg.css'],
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/moberghr/beacon' },
      ],
      editLink: {
        baseUrl: 'https://github.com/moberghr/beacon/edit/main/docs/site/',
      },
      components: {
        // Append the Moberg attribution beneath the default doc footer.
        Footer: './src/components/DocFooter.astro',
        // Light by default, dark only by choice (Moberg brand); never follows the OS.
        ThemeProvider: './src/components/overrides/ThemeProvider.astro',
        ThemeSelect: './src/components/overrides/ThemeSelect.astro',
        // "Copy as Markdown" / "View .md" beneath every page title.
        PageTitle: './src/components/overrides/PageTitle.astro',
        // Top navigation bar on every page (the landing and Explore pages have no sidebar).
        Header: './src/components/overrides/Header.astro',
      },
      head: [
        {
          tag: 'meta',
          attrs: { property: 'og:image', content: 'https://moberghr.github.io/beacon/img/screenshots/home-dark.png' },
        },
        {
          tag: 'meta',
          attrs: { name: 'twitter:card', content: 'summary_large_image' },
        },
      ],
      sidebar,
    }),
  ],
});
