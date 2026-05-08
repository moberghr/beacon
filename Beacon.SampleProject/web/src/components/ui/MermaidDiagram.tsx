import { useEffect, useId, useRef, useState } from 'react';

// Lazy-load mermaid only when this component mounts so it stays in its own chunk.
let mermaidPromise: Promise<typeof import('mermaid').default> | null = null;

function loadMermaid() {
  if (mermaidPromise === null) {
    mermaidPromise = import('mermaid').then(mod => {
      const mermaid = mod.default;
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: 'strict',
        theme: 'neutral',
        fontFamily: 'inherit',
      });
      return mermaid;
    });
  }
  return mermaidPromise;
}

interface MermaidDiagramProps {
  chart: string;
  className?: string;
}

/**
 * Renders a Mermaid diagram from a chart definition string.
 * `mermaid` is dynamically imported on first mount so it ends up in its own
 * chunk. Initialization runs once per page lifetime with `securityLevel: 'strict'`.
 */
export function MermaidDiagram({ chart, className }: MermaidDiagramProps) {
  const reactId = useId();
  const safeId = `mermaid-${reactId.replace(/[:]/g, '')}`;
  const containerRef = useRef<HTMLDivElement>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);

    loadMermaid()
      .then(mermaid => mermaid.render(safeId, chart))
      .then(({ svg }) => {
        if (cancelled) return;
        if (containerRef.current) {
          containerRef.current.innerHTML = svg;
        }
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        const message = err instanceof Error ? err.message : 'Failed to render diagram';
        setError(message);
        if (containerRef.current) {
          containerRef.current.innerHTML = '';
        }
      });

    return () => {
      cancelled = true;
    };
  }, [chart, safeId]);

  if (error) {
    return (
      <div
        className={`card ${className ?? ''}`}
        style={{ padding: 12, borderColor: 'var(--crit)' }}
      >
        <div style={{ fontWeight: 600, marginBottom: 4 }}>Diagram failed to render</div>
        <div className="muted mono" style={{ fontSize: 12, whiteSpace: 'pre-wrap' }}>{error}</div>
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className={`mermaid-diagram ${className ?? ''}`}
      style={{ overflow: 'auto', padding: 8 }}
      role="img"
      aria-label="Diagram"
    />
  );
}

export default MermaidDiagram;
