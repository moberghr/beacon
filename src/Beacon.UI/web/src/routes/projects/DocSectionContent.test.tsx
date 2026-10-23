import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { DocSectionContent } from './DocSectionContent';

function html(content: string): string {
  const { container } = render(<DocSectionContent content={content} />);
  return container.querySelector('.doc-section-markdown')?.innerHTML ?? '';
}

describe('DocSectionContent sanitising', () => {
  it('drops scripts, event attributes, style, forms and unsafe urls', () => {
    const out = html(
      [
        '<script>window.x=1</script>',
        '<img src="https://example.com/a.png" onerror="window.x=1">',
        '<p style="color:red">styled</p>',
        '<form action="https://example.com"><input name="a"></form>',
        '[js](javascript:alert(1))',
        '[data](data:text/html;base64,AAAA)',
        '[ftp](ftp://example.com/a)',
        '[ok](https://example.com) [mail](mailto:a@example.com)',
      ].join('\n\n'),
    );
    expect(out).not.toContain('<script');
    expect(out).not.toContain('onerror');
    expect(out).not.toContain('style=');
    expect(out).not.toContain('<form');
    expect(out).not.toContain('<input');
    expect(out).not.toContain('javascript:');
    expect(out).not.toContain('data:text');
    expect(out).not.toContain('ftp:');
    expect(out).toContain('https://example.com');
    expect(out).toContain('mailto:a@example.com');
  });
});
