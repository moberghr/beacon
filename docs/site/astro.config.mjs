// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';

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

// Beacon documentation — Astro + Starlight, Moberg house brand (blue #430cda).
// Deployed to GitHub Pages at https://moberghr.github.io/beacon/
export default defineConfig({
  site: 'https://moberghr.github.io',
  base: BASE,
  trailingSlash: 'ignore',
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
        'Semantic database monitoring, alerting, and orchestration for .NET — with a governed MCP server for AI assistants.',
      logo: {
        src: './src/assets/beacon-logo.svg',
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
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'Overview', link: '/getting-started/' },
            { label: 'Installation', link: '/getting-started/installation/' },
            { label: 'Quick Start', link: '/getting-started/quick-start/' },
            { label: 'Configuration', link: '/getting-started/configuration/' },
          ],
        },
        {
          label: 'Core Features',
          items: [
            { label: 'Data Sources', link: '/features/data-sources/' },
            { label: 'Queries', link: '/features/queries/' },
            { label: 'Subscriptions', link: '/features/subscriptions/' },
            { label: 'Notifications', link: '/features/notifications/' },
            { label: 'Data Migration', link: '/features/data-migration/' },
          ],
        },
        {
          label: 'Operations & Quality',
          items: [
            { label: 'Control Tower', link: '/features/control-tower/' },
            { label: 'Tasks', link: '/features/tasks/' },
            { label: 'Anomaly Detection', link: '/features/anomaly-detection/' },
            { label: 'Data Quality', link: '/features/data-quality/' },
          ],
        },
        {
          label: 'AI & MCP',
          items: [
            { label: 'MCP Server', link: '/features/mcp-server/' },
            { label: 'AI Integration', link: '/features/ai-integration/' },
            { label: 'AI Actors', link: '/features/ai-actors/' },
          ],
        },
        {
          label: 'Platform & Admin',
          items: [
            { label: 'User Management', link: '/features/user-management/' },
            { label: 'Authorization', link: '/features/authorization/' },
            { label: 'API Keys', link: '/features/api-keys/' },
            { label: 'Admin Settings', link: '/features/admin-settings/' },
          ],
        },
      ],
    }),
  ],
});
