import { useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button, Pill } from '@/components/beacon';
import { Pager } from '@/components/data/Pager';
import { Tabs } from '@/components/Tabs';
import { useAccess } from '@/auth/useAuth';
import { describeError } from '@/lib/api';
import { OwnedResourceKind } from '@/lib/enums';
import { formatDate } from '@/lib/format';
import { OWNED_RESOURCES_PAGE_SIZE, useOwnedResources } from './queries';

/** The kinds of resource listed, one tab each, in the order the server numbers them. */
export const OWNED_RESOURCE_TABS: { kind: OwnedResourceKind; label: string }[] = [
  { kind: OwnedResourceKind.Query, label: 'Queries' },
  { kind: OwnedResourceKind.Subscription, label: 'Subscriptions' },
  { kind: OwnedResourceKind.DataContract, label: 'Data contracts' },
  { kind: OwnedResourceKind.Recipient, label: 'Recipients' },
  { kind: OwnedResourceKind.MigrationJob, label: 'Migration jobs' },
];

/**
 * What the user owns, created or changed last, one kind of resource per tab and paged, so it can be handed over before
 * they leave. For an Auditor or a UserAdmin (the server refuses everyone else); read only once shown.
 */
export function OwnedResources({ userId }: { userId: number }) {
  const access = useAccess();
  const allowed = access.hasAnyPermission('Auditor', 'UserAdmin');
  const [shown, setShown] = useState(false);
  const [kind, setKind] = useState<OwnedResourceKind>(OwnedResourceKind.Query);
  const [page, setPage] = useState(0);
  const query = useOwnedResources(userId, kind, page, allowed && shown);

  if (!allowed) return null;

  const items = query.data?.items ?? [];

  return (
    <section aria-label="Owned resources" className="flex flex-col gap-2 mt-4 pt-3 border-t border-border">
      <div className="flex items-center justify-between gap-3">
        <h3 className="m-0 text-sm font-semibold text-text">Owned resources</h3>
        <Button type="button" size="sm" aria-expanded={shown} onClick={() => setShown(x => !x)}>
          {shown ? 'Hide' : 'Show owned resources'}
        </Button>
      </div>
      <p className="m-0 text-xs text-text-muted">
        What this user owns, created or changed last, to hand over before they leave. Scheduled runs act with the owner&apos;s
        access. Archived resources are not listed; a resource in a project you cannot read is listed without its name.
      </p>
      {shown && (
        <>
          <Tabs
            tabs={OWNED_RESOURCE_TABS.map(x => ({ key: String(x.kind), label: x.label }))}
            active={String(kind)}
            onChange={key => {
              setKind(Number(key) as OwnedResourceKind);
              setPage(0);
            }}
          />
          {query.isError ? (
            <div className="flex items-center gap-2 text-xs text-crit">
              <AlertTriangle className="size-3.5" />
              <span>{describeError(query.error, 'The owned resources could not be loaded.')}</span>
            </div>
          ) : query.isLoading ? (
            <div className="text-xs text-text-muted">Loading…</div>
          ) : items.length === 0 ? (
            <div className="text-xs text-text-muted">None.</div>
          ) : (
            <ul aria-label="Resources" className="m-0 p-0 list-none flex flex-col divide-y divide-border">
              {items.map(x => (
                <li key={x.id} className="flex items-center gap-2 py-1.5 text-sm">
                  <span className="mono text-xs text-text-muted">#{x.id}</span>
                  {x.nameWithheld ? (
                    <span
                      className="flex-1 min-w-0 truncate italic text-text-muted"
                      title="In a project you cannot read: listed by id and kind only"
                    >
                      Name withheld
                    </span>
                  ) : (
                    <span className="flex-1 min-w-0 truncate text-text">{x.name}</span>
                  )}
                  {x.isOwner && <Pill tone="info">Owner</Pill>}
                  {x.isCreator && <Pill>Creator</Pill>}
                  {x.isLastModifier && <Pill>Last change</Pill>}
                  <span className="text-xs text-text-muted" title="Created · last changed">
                    {formatDate(x.createdTime)}
                    {x.modifiedTime ? ` · ${formatDate(x.modifiedTime)}` : ''}
                  </span>
                </li>
              ))}
            </ul>
          )}
          <Pager
            page={page}
            pageSize={OWNED_RESOURCES_PAGE_SIZE}
            pageCount={query.data?.pageCount ?? 0}
            totalCount={query.data?.totalCount ?? 0}
            onPageChange={setPage}
            className="px-0"
          />
        </>
      )}
    </section>
  );
}
