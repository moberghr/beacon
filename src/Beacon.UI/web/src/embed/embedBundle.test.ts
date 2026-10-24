import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

const webRoot = path.resolve(__dirname, '../..');
const embedDir = __dirname;

describe('embed bundle', () => {
  it('imports none of the app: only React and its own modules', () => {
    const sources = fs
      .readdirSync(embedDir)
      .filter(x => /\.(ts|tsx)$/.test(x))
      .filter(x => !/\.test\.(ts|tsx)$/.test(x));

    for (const file of sources) {
      const text = fs.readFileSync(path.join(embedDir, file), 'utf8');
      const specifiers = [...text.matchAll(/^\s*import\s+(?:[^'";]*?\sfrom\s+)?['"]([^'"]+)['"]/gm)].map(x => x[1]);
      for (const specifier of specifiers) {
        const allowed = ['react', 'react-dom/client', '../index.css'].includes(specifier) || /^\.\/\w+$/.test(specifier);
        expect(allowed, `${file} imports ${specifier}`).toBe(true);
      }
    }
  });

  it('has a page without inline scripts or styles, which the embed CSP refuses', () => {
    const html = fs.readFileSync(path.join(webRoot, 'embed.html'), 'utf8');
    const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];

    expect(scripts).toHaveLength(1);
    expect(scripts[0][1]).toContain('src="/src/embed/main.tsx"');
    expect(scripts[0][2].trim()).toBe('');
    expect(html).not.toMatch(/<style\b/);
    expect(html).not.toMatch(/\sstyle=/);
    expect(html).not.toMatch(/\son[a-z]+=/);
  });

  it('keeps the token out of browser storage', () => {
    for (const file of fs.readdirSync(embedDir).filter(x => !/\.test\./.test(x))) {
      const text = fs.readFileSync(path.join(embedDir, file), 'utf8');
      expect(text, file).not.toMatch(/localStorage|sessionStorage|indexedDB|document\.cookie/);
    }
  });
});
