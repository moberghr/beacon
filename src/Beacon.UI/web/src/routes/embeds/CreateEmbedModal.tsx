import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { Banner, Button, Field, Input, Select, Textarea } from '@/components/beacon';
import { describeError } from '@/lib/api';
import { ProjectPicker } from '@/routes/projects/ProjectPicker';
import type { ProjectSummaryEntry } from '@/routes/projects/queries';
import { useQueriesListQuery, useQueryDetailQuery } from '@/routes/queries/queries';
import { useCreateEmbedGrant, type CreateEmbedGrantResult } from './queries';

/** The lifetime proposed for a new grant; the deployment caps it (`Beacon:Embed:MaxGrantDays`, 90 by default). */
const DEFAULT_EXPIRES_DAYS = 30;

type ParameterMode = 'locked' | 'fixed';

interface ParameterChoice {
  mode: ParameterMode;
  value: string;
}

interface CreateEmbedModalProps {
  open: boolean;
  onClose: () => void;
}

/**
 * Creates an embed grant: a project, one of its queries with an approved version, the origins that may frame it, an
 * expiry, and every query parameter either fixed here or locked (supplied by the host's backend at each exchange). The
 * grant secret is shown once, with the exchange call a host's backend makes; it is never kept after the dialog closes.
 */
export function CreateEmbedModal({ open, onClose }: CreateEmbedModalProps) {
  const create = useCreateEmbedGrant();
  const [name, setName] = useState('');
  const [project, setProject] = useState<ProjectSummaryEntry | null>(null);
  const [querySearch, setQuerySearch] = useState('');
  const [queryId, setQueryId] = useState<number | null>(null);
  const [origins, setOrigins] = useState('');
  const [expiresInDays, setExpiresInDays] = useState(String(DEFAULT_EXPIRES_DAYS));
  const [choices, setChoices] = useState<Record<string, ParameterChoice>>({});
  const [error, setError] = useState<string | null>(null);
  // The secret lives in component state only while the dialog shows it (§1.3: never persisted or logged).
  const [created, setCreated] = useState<CreateEmbedGrantResult | null>(null);
  const [copied, setCopied] = useState(false);

  const queries = useQueriesListQuery({ searchTerm: querySearch.trim() || undefined, pageSize: 20 });
  const query = useQueryDetailQuery(queryId ?? undefined);
  const runnable =
    query.data != null && query.data.mcpToolRunnableVersionNumber != null && query.data.mcpToolIssue == null;

  // Each distinct parameter of the query, by name: the server requires every one fixed or locked.
  const parameters = useMemo(() => {
    const seen = new Set<string>();
    return (query.data?.steps ?? [])
      .flatMap(x => x.parameters)
      .filter(x => {
        if (seen.has(x.name)) return false;
        seen.add(x.name);
        return true;
      });
  }, [query.data]);

  useEffect(() => {
    if (open) {
      setName('');
      setProject(null);
      setQuerySearch('');
      setQueryId(null);
      setOrigins('');
      setExpiresInDays(String(DEFAULT_EXPIRES_DAYS));
      setChoices({});
      setError(null);
      setCreated(null);
      setCopied(false);
    }
  }, [open]);

  const handleClose = () => {
    setCreated(null);
    setCopied(false);
    // Drop the mutation's result too, or the secret would linger in the query client after the dialog closes.
    create.reset();
    onClose();
  };

  const choiceOf = (parameter: string): ParameterChoice => choices[parameter] ?? { mode: 'locked', value: '' };
  const setChoice = (parameter: string, choice: ParameterChoice) => setChoices(x => ({ ...x, [parameter]: choice }));

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const allowedOrigins = origins
      .split(/\s+/)
      .map(x => x.trim())
      .filter(x => x.length > 0);
    const days = Number(expiresInDays);
    if (name.trim() === '') return setError('Name the grant.');
    if (project == null) return setError('Pick a project.');
    if (queryId == null || !runnable) return setError('Pick a query with an approved version.');
    if (allowedOrigins.length === 0) return setError('Name at least one origin that may frame the view.');
    if (!Number.isInteger(days) || days < 1) return setError('The grant expires in a whole number of days, at least 1.');

    const fixedParameters: Record<string, string> = {};
    const lockedParameters: string[] = [];
    for (const parameter of parameters) {
      const choice = choiceOf(parameter.name);
      if (choice.mode === 'fixed') {
        fixedParameters[parameter.name] = choice.value;
      } else {
        lockedParameters.push(parameter.name);
      }
    }

    try {
      const result = await create.mutateAsync({
        projectId: project.id,
        queryId,
        name: name.trim(),
        allowedOrigins,
        expiresInDays: days,
        fixedParameters: Object.keys(fixedParameters).length > 0 ? fixedParameters : null,
        lockedParameters: lockedParameters.length > 0 ? lockedParameters : null,
      });
      setCreated(result);
    } catch (err) {
      setError(describeError(err, 'Create embed grant failed'));
    }
  };

  const copySecret = async () => {
    if (created == null) return;
    try {
      await navigator.clipboard.writeText(created.secret);
      setCopied(true);
    } catch {
      setError('Could not copy automatically — select and copy the secret manually.');
    }
  };

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      title={created ? 'Embed grant created' : 'New embed grant'}
      sub={
        created
          ? 'Copy the grant secret now. It will not be shown again.'
          : 'A read-only view of one approved query, framed by the sites you name.'
      }
      size="lg"
      closeOnBackdrop={!created}
      closeOnEscape={!created}
      footer={
        created ? (
          <Button type="button" variant="primary" onClick={handleClose}>
            Done — I've copied the secret
          </Button>
        ) : (
          <>
            <Button type="button" onClick={handleClose} disabled={create.isPending}>
              Cancel
            </Button>
            <Button type="submit" form="embed-grant-form" variant="primary" disabled={create.isPending}>
              {create.isPending ? 'Creating…' : 'Create grant'}
            </Button>
          </>
        )
      }
    >
      {created ? (
        <CreatedGrant created={created} copied={copied} onCopy={copySecret} />
      ) : (
        <form id="embed-grant-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3.5">
          {error && <Banner tone="crit" icon={<AlertTriangle />} title={error} />}

          <Field label="Name">
            <Input value={name} maxLength={100} placeholder="e.g. Partner portal orders" onChange={e => setName(e.target.value)} />
          </Field>

          <div className="flex flex-col gap-1.5">
            <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Project</span>
            <ProjectPicker value={project?.id} onSelect={setProject} />
          </div>

          <div className="flex flex-col gap-1.5">
            <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Query</span>
            <Input
              aria-label="Search queries"
              placeholder="Search queries…"
              value={querySearch}
              onChange={e => setQuerySearch(e.target.value)}
            />
            <Select
              aria-label="Query"
              value={queryId ?? ''}
              onChange={e => {
                setQueryId(e.target.value === '' ? null : Number(e.target.value));
                setChoices({});
              }}
            >
              <option value="">Pick a query…</option>
              {(queries.data?.items ?? []).map(x => (
                <option key={x.queryId} value={x.queryId}>
                  {x.name}
                </option>
              ))}
            </Select>
            {queryId != null && query.data && !runnable && (
              <span className="text-xs text-crit">
                {query.data.mcpToolIssue ?? 'This query has no approved version; only an approved version can be embedded.'}
              </span>
            )}
            {queryId != null && runnable && (
              <span className="text-xs text-text-subtle">
                Version {query.data!.mcpToolRunnableVersionNumber} is embedded. A newer approved version stops the grant.
              </span>
            )}
          </div>

          <Field label="Allowed origins" hint="One per line, e.g. https://portal.example.com. No paths or wildcards.">
            <Textarea rows={3} value={origins} onChange={e => setOrigins(e.target.value)} />
          </Field>

          <Field
            label="Expires in (days)"
            hint="The deployment caps it (90 days unless configured otherwise). Re-create the grant to extend it."
          >
            <Input
              type="number"
              min={1}
              value={expiresInDays}
              onChange={e => setExpiresInDays(e.target.value)}
            />
          </Field>

          {runnable && parameters.length > 0 && (
            <div className="flex flex-col gap-2">
              <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Parameters</span>
              <span className="text-xs text-text-subtle">
                Viewers cannot change any of them. A locked value is sent by your backend with each token and is readable
                in the token: never lock a secret.
              </span>
              {parameters.map(parameter => {
                const choice = choiceOf(parameter.name);
                return (
                  <div key={parameter.name} className="grid grid-cols-[1fr_8rem_1fr] items-center gap-2">
                    <code className="text-xs">{parameter.name}</code>
                    <Select
                      aria-label={`${parameter.name} mode`}
                      value={choice.mode}
                      onChange={e => setChoice(parameter.name, { ...choice, mode: e.target.value as ParameterMode })}
                    >
                      <option value="locked">Locked</option>
                      <option value="fixed">Fixed</option>
                    </Select>
                    {choice.mode === 'fixed' ? (
                      <Input
                        aria-label={`${parameter.name} value`}
                        value={choice.value}
                        onChange={e => setChoice(parameter.name, { ...choice, value: e.target.value })}
                      />
                    ) : (
                      <span className="text-xs text-text-muted">supplied at each exchange</span>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </form>
      )}
    </Dialog>
  );
}

function CreatedGrant({
  created,
  copied,
  onCopy,
}: {
  created: CreateEmbedGrantResult;
  copied: boolean;
  onCopy: () => void;
}) {
  const origin = window.location.origin;
  const parameters = created.lockedParameters.map(x => `"${x}": "<value>"`).join(', ');
  const exchange = [
    `curl -X POST ${origin}/beacon/api/embed/tokens \\`,
    `  -H "Authorization: BeaconEmbedGrant $BEACON_EMBED_SECRET" \\`,
    `  -H "Content-Type: application/json" \\`,
    `  -d '{"parameters": {${parameters}}}'`,
  ].join('\n');

  return (
    <div className="grid gap-3">
      <Banner
        tone="crit"
        icon={<AlertTriangle />}
        title="This secret will not be shown again."
        sub="Store it in your backend's secret store. It never belongs in a browser."
      />
      <div className="flex items-center gap-2 rounded-md border border-border bg-surface-2 p-3">
        <div className="flex-1 break-all font-mono text-sm">{created.secret}</div>
        <Button type="button" variant="primary" onClick={onCopy}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
      <div className="flex flex-col gap-1.5">
        <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
          Exchange it for a token, per page view (server side)
        </span>
        <pre className="overflow-auto rounded-md border border-border bg-surface-2 p-3 text-xs">{exchange}</pre>
        <span className="text-xs text-text-subtle">
          Frame the returned <code>embedPath</code> under {origin} in an iframe. The token in it expires within minutes;
          the page posts <code>{'{ type: "beacon-embed:expired" }'}</code> to your page when it has.
        </span>
      </div>
    </div>
  );
}
