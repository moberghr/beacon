import { Info } from 'lucide-react';
import { Pill } from '@/components/beacon';
import { ChangeApprovalKind, type MigrationMode } from '@/lib/enums';
import { MIGRATION_MODE_LABEL } from '@/routes/migration-history/queries';
import { NOTIFICATION_TYPE_LABEL } from '@/routes/recipients/queries';
import type {
  ChangeApprovalDetail,
  CustomSqlRuleView,
  CustomSqlRulesView,
  MigrationJobDefinitionView,
  RecipientDestinationView,
} from './queries';

/** What a change of a migration job, a data contract's Custom SQL rules or a recipient would put in place. */
export function ChangeApprovalDiff({ detail }: { detail: ChangeApprovalDetail }) {
  switch (detail.kind) {
    case ChangeApprovalKind.MigrationJob:
      return <MigrationJobDiff previous={detail.previousMigrationJob} proposed={detail.proposedMigrationJob} />;
    case ChangeApprovalKind.CustomSqlRules:
      return <CustomSqlDiff previous={detail.previousCustomSqlRules} proposed={detail.proposedCustomSqlRules} />;
    case ChangeApprovalKind.RecipientDestination:
      return (
        <RecipientDiff previous={detail.previousRecipientDestination} proposed={detail.proposedRecipientDestination} />
      );
    default:
      return <div className="text-sm text-text-muted">This kind of change cannot be shown.</div>;
  }
}

function MigrationJobDiff({
  previous,
  proposed,
}: {
  previous: MigrationJobDefinitionView | null;
  proposed: MigrationJobDefinitionView | null;
}) {
  if (!proposed) {
    return <div className="text-sm text-text-muted">The proposed definition cannot be read.</div>;
  }

  const mode = (x: MigrationJobDefinitionView) => MIGRATION_MODE_LABEL[x.mode as MigrationMode] ?? `mode ${x.mode}`;
  const source = (x: MigrationJobDefinitionView) => x.sourceDataSourceName ?? `Data source #${x.sourceDataSourceId}`;
  const destination = (x: MigrationJobDefinitionView) =>
    x.destinationDataSourceName ?? `Data source #${x.destinationDataSourceId}`;

  return (
    <section aria-label="Migration job definition" className="flex flex-col gap-3">
      {!previous && (
        <div className="text-xs text-text-muted">
          The job is new: everything below is what it will do once approved. It does not run before.
        </div>
      )}
      <dl className="grid grid-cols-[160px_1fr] gap-x-3 gap-y-1.5 text-sm m-0">
        <Field label="Source" before={previous && source(previous)} after={source(proposed)} />
        <Field label="Destination" before={previous && destination(previous)} after={destination(proposed)} />
        <Field label="Destination table" before={previous?.destinationTable} after={proposed.destinationTable} mono />
        <Field label="Mode" before={previous && mode(previous)} after={mode(proposed)} />
      </dl>
      <CodeChange label="Source query" before={previous?.queryText ?? null} after={proposed.queryText} hasBefore={previous != null} />
      {(proposed.transformationScript || previous?.transformationScript) && (
        <CodeChange
          label="Transformation script"
          before={previous?.transformationScript ?? null}
          after={proposed.transformationScript ?? ''}
          hasBefore={previous != null}
        />
      )}
    </section>
  );
}

function CustomSqlDiff({ previous, proposed }: { previous: CustomSqlRulesView | null; proposed: CustomSqlRulesView | null }) {
  if (!proposed) {
    return <div className="text-sm text-text-muted">The proposed rules cannot be read.</div>;
  }

  const sourceName = (x: CustomSqlRulesView) => x.dataSourceName ?? `Data source #${x.dataSourceId}`;
  const before = new Map((previous?.rules ?? []).map(x => [x.name, x]));
  const kept = new Set(proposed.rules.map(x => x.name));
  const removed = (previous?.rules ?? []).filter(x => !kept.has(x.name));

  return (
    <div className="flex flex-col gap-3">
      <dl className="grid grid-cols-[160px_1fr] gap-x-3 gap-y-1.5 text-sm m-0">
        <Field label="Data source" before={previous && sourceName(previous)} after={sourceName(proposed)} />
      </dl>
      {!previous && (
        <div className="text-xs text-text-muted">The contract is new: its Custom SQL rules run only once approved.</div>
      )}
      {proposed.rules.length === 0 && <div className="text-sm text-text-muted">No Custom SQL rules remain.</div>}
      {proposed.rules.map(rule => (
        <RuleBlock key={rule.name} rule={rule} previous={before.get(rule.name) ?? null} />
      ))}
      {removed.map(rule => (
        <section key={`removed-${rule.name}`} aria-label={`Rule ${rule.name}`} className="border border-border rounded-sm p-3">
          <div className="flex items-center gap-2 mb-2">
            <span className="text-xs font-semibold">{rule.name}</span>
            <Pill tone="crit">Removed</Pill>
          </div>
          <Code label="Removed SQL" text={rule.sql} />
        </section>
      ))}
    </div>
  );
}

function RuleBlock({ rule, previous }: { rule: CustomSqlRuleView; previous: CustomSqlRuleView | null }) {
  const changed = previous != null && previous.sql !== rule.sql;

  return (
    <section aria-label={`Rule ${rule.name}`} className="border border-border rounded-sm p-3">
      <div className="flex items-center gap-2 mb-2">
        <span className="text-xs font-semibold">{rule.name}</span>
        {rule.awaitsApproval ? (
          <Pill tone="warn">{previous == null ? 'New: awaits approval' : 'Awaits approval'}</Pill>
        ) : (
          <Pill tone="neutral">Unchanged</Pill>
        )}
      </div>
      {changed ? (
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-2">
          <Code label="Current SQL" text={previous.sql} />
          <Code label="Proposed SQL" text={rule.sql} />
        </div>
      ) : (
        <Code label={rule.awaitsApproval ? 'Proposed SQL' : 'SQL'} text={rule.sql} />
      )}
    </section>
  );
}

function RecipientDiff({
  previous,
  proposed,
}: {
  previous: RecipientDestinationView | null;
  proposed: RecipientDestinationView | null;
}) {
  if (!proposed || proposed.unreadable) {
    return <div className="text-sm text-text-muted">The proposed destination cannot be read.</div>;
  }

  const type = (x: RecipientDestinationView) => NOTIFICATION_TYPE_LABEL[x.notificationType] ?? `type ${x.notificationType}`;
  const readablePrevious = previous != null && !previous.unreadable ? previous : null;

  return (
    <section aria-label="Recipient destination" className="flex flex-col gap-3">
      {!previous && (
        <div className="text-xs text-text-muted">The recipient is new: nothing is delivered to it before it is approved.</div>
      )}
      {previous?.unreadable && (
        <div className="text-xs text-text-muted">Where it delivers now cannot be read: only the proposed destination is shown.</div>
      )}
      <dl className="grid grid-cols-[160px_1fr] gap-x-3 gap-y-1.5 text-sm m-0">
        <Field label="Type" before={readablePrevious && type(readablePrevious)} after={type(proposed)} />
        <Field label="Destination" before={readablePrevious?.destination} after={proposed.destination} mono />
        <Field
          label="Fingerprint"
          before={readablePrevious?.fingerprint ?? null}
          after={proposed.fingerprint ?? 'cannot be read'}
          mono
        />
      </dl>
      <HeaderFingerprints previous={readablePrevious} proposed={proposed} />
      <div className="flex items-start gap-2 text-xs text-text-muted">
        <Info className="size-3.5 mt-0.5 shrink-0" />
        <span>
          Credentials are masked: user info, query values, token-like path segments, tokens and signatures, and header
          values. A fingerprint is a short keyed digest of the value: equal fingerprints mean equal values, and it reveals
          nothing else, so you can compare it with the requester out of band. The recipients page shows its editors the
          same fingerprints.
        </span>
      </div>
    </section>
  );
}

/** The custom headers by name with the fingerprint of each value, before and after: added, removed and changed ones marked. */
function HeaderFingerprints({
  previous,
  proposed,
}: {
  previous: RecipientDestinationView | null;
  proposed: RecipientDestinationView;
}) {
  const after = proposed.headers ?? proposed.headerNames.map(x => ({ name: x, fingerprint: '' }));
  const before = new Map((previous?.headers ?? []).map(x => [x.name, x.fingerprint]));
  const kept = new Set(after.map(x => x.name));
  const removed = (previous?.headers ?? []).filter(x => !kept.has(x.name));

  if (after.length === 0 && removed.length === 0) {
    return <div className="text-xs text-text-muted">No custom headers.</div>;
  }

  return (
    <div>
      <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">Custom headers</div>
      <ul className="m-0 p-0 list-none flex flex-col gap-1 text-sm" aria-label="Custom headers">
        {after.map(x => {
          const was = before.get(x.name);
          const state = previous == null ? null : was === undefined ? 'Added' : was !== x.fingerprint ? 'Value changed' : null;

          return (
            <li key={x.name} className="flex flex-wrap items-center gap-2">
              <span className="mono text-xs">{x.name}</span>
              {x.fingerprint && <span className="mono text-xs text-text-muted">{x.fingerprint}</span>}
              {state && <Pill tone="warn">{state}</Pill>}
              {state === 'Value changed' && <span className="mono text-xs text-text-muted line-through">{was}</span>}
            </li>
          );
        })}
        {removed.map(x => (
          <li key={`removed-${x.name}`} className="flex flex-wrap items-center gap-2">
            <span className="mono text-xs line-through text-text-muted">{x.name}</span>
            <Pill tone="crit">Removed</Pill>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Field({
  label,
  before,
  after,
  mono = false,
}: {
  label: string;
  before?: string | null | false;
  after: string;
  mono?: boolean;
}) {
  const changed = before != null && before !== false && before !== after;

  return (
    <>
      <dt className="text-text-muted">{label}</dt>
      <dd className={mono ? 'm-0 mono text-xs' : 'm-0'}>
        {changed ? (
          <span>
            <span className="line-through text-text-muted">{before}</span> → <span className="font-medium">{after}</span>
          </span>
        ) : (
          after
        )}
      </dd>
    </>
  );
}

function CodeChange({
  label,
  before,
  after,
  hasBefore,
}: {
  label: string;
  before: string | null;
  after: string;
  hasBefore: boolean;
}) {
  if (hasBefore && before !== after) {
    return (
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-2">
        <Code label={`Current ${label.toLowerCase()}`} text={before ?? ''} />
        <Code label={`Proposed ${label.toLowerCase()}`} text={after} />
      </div>
    );
  }

  return <Code label={label} text={after} />;
}

function Code({ label, text }: { label: string; text: string }) {
  return (
    <div>
      <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted mb-1">{label}</div>
      <pre className="bg-surface-2 border border-border rounded-sm p-2.5 text-xs m-0 whitespace-pre-wrap overflow-auto max-h-80 mono">
        {text || <span className="text-text-muted">(empty)</span>}
      </pre>
    </div>
  );
}
