import { useMemo } from 'react';
import { marked } from 'marked';
import { MermaidDiagram } from '@/components/ui/MermaidDiagram';

interface DocSectionContentProps {
  content: string;
}

interface Block {
  kind: 'text' | 'mermaid';
  content: string;
  key: number;
}

const FENCE_RE = /```mermaid\s*\n([\s\S]*?)```/g;

function splitContent(raw: string): Block[] {
  const blocks: Block[] = [];
  let cursor = 0;
  let key = 0;
  let match: RegExpExecArray | null;

  // Reset lastIndex on the regex object before iterating.
  FENCE_RE.lastIndex = 0;

  while ((match = FENCE_RE.exec(raw)) !== null) {
    if (match.index > cursor) {
      blocks.push({ kind: 'text', content: raw.slice(cursor, match.index), key: key++ });
    }
    blocks.push({ kind: 'mermaid', content: match[1].trim(), key: key++ });
    cursor = match.index + match[0].length;
  }
  if (cursor < raw.length) {
    blocks.push({ kind: 'text', content: raw.slice(cursor), key: key++ });
  }
  if (blocks.length === 0) {
    blocks.push({ kind: 'text', content: '', key: 0 });
  }
  return blocks;
}

function MarkdownBlock({ content }: { content: string }) {
  const html = useMemo(() => {
    try {
      return marked.parse(content, { async: false }) as string;
    } catch {
      return `<pre>${content}</pre>`;
    }
  }, [content]);

  return (
    <div
      // eslint-disable-next-line react/no-danger
      dangerouslySetInnerHTML={{ __html: html }}
      style={{
        fontSize: 14,
        lineHeight: 1.65,
        color: 'var(--text)',
      }}
      className="doc-section-markdown"
    />
  );
}

/**
 * Renders documentation section content. Splits ```mermaid fences out and
 * renders them with `<MermaidDiagram>`. Text blocks are rendered as markdown
 * using `marked`.
 */
export function DocSectionContent({ content }: DocSectionContentProps) {
  const blocks = splitContent(content ?? '');

  return (
    <div className="doc-section-content">
      {blocks.map(b =>
        b.kind === 'mermaid'
          ? <MermaidDiagram key={b.key} chart={b.content} />
          : <MarkdownBlock key={b.key} content={b.content} />,
      )}
    </div>
  );
}

export default DocSectionContent;
