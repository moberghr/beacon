import { Zap } from 'lucide-react';
import { Card, CardHead, CardTitle, CardSub, CardBody } from '@/components/beacon';
import type { QueryDetail } from '../queries';

export function FinalQueryCard({ query }: { query: QueryDetail }) {
  if (!query.finalQuery) return null;

  return (
    <Card>
      <CardHead>
        <Zap className="size-3.5 text-text-muted" />
        <CardTitle>Final query</CardTitle>
        <CardSub>runs against in-memory join layer</CardSub>
      </CardHead>
      <CardBody flush>
        <pre className="m-0 p-3 bg-surface-2 mono text-xs overflow-auto whitespace-pre-wrap max-h-80">
          {query.finalQuery}
        </pre>
      </CardBody>
    </Card>
  );
}
