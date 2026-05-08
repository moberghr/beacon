import { Icon } from '@/components/Icon';
import type { QueryDetail } from '../queries';

export function FinalQueryCard({ query }: { query: QueryDetail }) {
  if (!query.finalQuery) return null;

  return (
    <div className="card">
      <div className="card__head">
        <Icon.Bolt size={15} className="muted" />
        <h3 className="card__title">Final query</h3>
        <span className="card__sub">runs against in-memory join layer</span>
      </div>
      <div className="card__body">
        <pre
          className="sql__pre"
          style={{
            padding: 12,
            background: 'var(--surface-2)',
            borderRadius: 6,
            fontSize: 12,
            overflow: 'auto',
            whiteSpace: 'pre-wrap',
            maxHeight: 320,
          }}
        >
          {query.finalQuery}
        </pre>
      </div>
    </div>
  );
}
