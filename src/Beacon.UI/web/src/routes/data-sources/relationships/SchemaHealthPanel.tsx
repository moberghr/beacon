import { AlertTriangle, GitFork, Unlink } from 'lucide-react';
import { Card, CardBody, CardHead, CardSub, CardTitle, KPI, KPIGrid, Pill } from '@/components/beacon';
import type { SchemaHealth } from './queries';

interface SchemaHealthPanelProps {
  health: SchemaHealth | undefined;
  isLoading: boolean;
}

/**
 * Connectivity report for the schema graph. Disconnected islands and isolated tables are where a
 * user must declare relationships by hand — on a warehouse with no enforced foreign keys that is
 * most of the schema until inference or manual registration fills it in.
 */
export function SchemaHealthPanel({ health, isLoading }: SchemaHealthPanelProps) {
  if (isLoading) {
    return (
      <Card>
        <CardBody>
          <p className="subtle text-sm">Loading schema health…</p>
        </CardBody>
      </Card>
    );
  }

  if (!health) {
    return null;
  }

  const isFragmented = health.componentCount > 1;

  return (
    <Card>
      <CardHead>
        <div>
          <CardTitle>Schema health</CardTitle>
          <CardSub>How well connected this data source&rsquo;s tables are</CardSub>
        </div>
      </CardHead>
      <CardBody className="space-y-4">
        <KPIGrid>
          <KPI dot="brand" label="Tables" value={health.tableCount} />
          <KPI dot="brand" label="Relationships" value={health.relationshipCount} />
          <KPI
            dot={health.unverifiedRelationshipCount > 0 ? 'warn' : 'ok'}
            label="Unverified"
            value={health.unverifiedRelationshipCount}
            sub={`${health.verifiedRelationshipCount} verified`}
          />
          <KPI
            dot={isFragmented ? 'warn' : 'ok'}
            label="Connected groups"
            value={health.componentCount}
            sub={`largest ${health.largestComponentSize}`}
          />
        </KPIGrid>

        {isFragmented && (
          <p className="flex items-start gap-2 text-sm text-warn">
            <AlertTriangle className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>
              This schema splits into {health.componentCount} disconnected groups. A question that
              spans two groups cannot be joined until a relationship between them is declared.
            </span>
          </p>
        )}

        {health.isolatedTables.length > 0 && (
          <section>
            <h4 className="eyebrow mb-2 flex items-center gap-1.5">
              <Unlink className="size-3.5" aria-hidden />
              Isolated tables ({health.isolatedTables.length})
            </h4>
            <div className="flex flex-wrap gap-1.5">
              {health.isolatedTables.map((table) => (
                <Pill key={table} tone="warn">
                  {table}
                </Pill>
              ))}
            </div>
            <p className="subtle mt-2 text-xs">
              These tables have no relationship to anything else — declare one below so questions can
              reach them.
            </p>
          </section>
        )}

        {health.junctionTables.length > 0 && (
          <section>
            <h4 className="eyebrow mb-2 flex items-center gap-1.5">
              <GitFork className="size-3.5" aria-hidden />
              Link tables ({health.junctionTables.length})
            </h4>
            <div className="flex flex-wrap gap-1.5">
              {health.junctionTables.map((table) => (
                <Pill key={table} tone="info">
                  {table}
                </Pill>
              ))}
            </div>
          </section>
        )}
      </CardBody>
    </Card>
  );
}
