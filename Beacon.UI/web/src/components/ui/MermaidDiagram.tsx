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
      <div className={`bg-surface border border-crit rounded-md p-3 ${className ?? ''}`}>
        <div className="font-semibold text-sm mb-1">Diagram failed to render</div>
        <div className="text-xs mono text-text-muted whitespace-pre-wrap">{error}</div>
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className={`overflow-auto p-2 ${className ?? ''}`}
      role="img"
      aria-label="Diagram"
    />
  );
}

export default MermaidDiagram;
