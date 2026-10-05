import { useEffect, useRef, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { ChevronRight } from 'lucide-react';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { Field, Input, Pill } from '@/components/beacon';
import { cn } from '@/lib/cn';
import { SearchMultiSelect } from '@/components/data/SearchMultiSelect';
import { NOTIFICATION_TYPE_LABEL, type RecipientEntry } from '@/routes/recipients/queries';
import { useQueriesListQuery, useQueryDetailQuery } from '@/routes/queries/queries';
import { useCreateSubscription } from './queries';

const SCHEMA = z.object({
  queryId: z.number({ message: 'Query id is required' }).int().min(1, 'Query id is required'),
  cronExpression: z.string().trim().min(1, 'Cron expression is required').max(200),
  recipientIds: z.array(z.number().int()),
  maxRows: z.number().int().min(0).nullable().optional(),
  timeoutSeconds: z.number().int().min(0).nullable().optional(),
  includeAttachment: z.boolean(),
  showQuery: z.boolean(),
  storeResults: z.boolean(),
  createTasks: z.boolean(),
})
  // Mirrors SubscriptionService: a task-tracking subscription needs no one to notify.
  .refine(x => x.createTasks || x.recipientIds.length > 0, {
    message: 'Pick at least one recipient, or enable Create tasks',
    path: ['recipientIds'],
  });

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  queryId: 0,
  cronExpression: '0 9 * * *',
  recipientIds: [],
  maxRows: null,
  timeoutSeconds: null,
  includeAttachment: false,
  showQuery: false,
  storeResults: false,
  createTasks: false,
};

interface AddSubscriptionDialogProps {
  open: boolean;
  onClose: () => void;
  initialQueryId?: number;
}

export function AddSubscriptionDialog({ open, onClose, initialQueryId }: AddSubscriptionDialogProps) {
  const createMutation = useCreateSubscription();

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: DEFAULTS,
    mode: 'onTouched',
  });
  const { register, watch, setValue, reset, formState: { errors } } = form;

  // The picked recipients themselves, so their names survive new searches; the form holds only ids.
  const [selectedRecipients, setSelectedRecipients] = useState<RecipientEntry[]>([]);

  useEffect(() => {
    if (!open) return;
    reset({ ...DEFAULTS, queryId: initialQueryId ?? 0 });
    setSelectedRecipients([]);
  }, [open, reset, initialQueryId]);

  const createTasks = watch('createTasks');
  const queryId = watch('queryId');
  const cronExpression = watch('cronExpression');

  const onRecipientsChange = (next: RecipientEntry[]) => {
    setSelectedRecipients(next);
    setValue('recipientIds', next.map(x => x.id), { shouldValidate: true });
  };

  const onFinish = async () => {
    const values = form.getValues();
    try {
      await createMutation.mutateAsync({
        queryId: values.queryId,
        cronExpression: values.cronExpression,
        recipientIds: values.recipientIds,
        maxRows: values.maxRows ?? null,
        timeoutSeconds: values.timeoutSeconds ?? null,
        includeAttachment: values.includeAttachment,
        showQuery: values.showQuery,
        storeResults: values.storeResults,
        createTasks: values.createTasks,
      });
      toast.success('Subscription created');
      onClose();
    } catch {
      // useCreateSubscription (createSimpleMutation) already toasts the error.
    }
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'query',
      title: 'Query',
      description: 'Pick a query and schedule.',
      fields: ['queryId', 'cronExpression'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field
            label={<>Query <span className="text-crit">*</span></>}
          >
            <QueryPicker
              value={queryId || null}
              onChange={id => setValue('queryId', id ?? 0, { shouldValidate: true })}
              hasError={!!errors.queryId}
            />
            {errors.queryId && <span className="text-xs text-crit">{errors.queryId.message}</span>}
          </Field>

          <Field
            label={<>Cron expression <span className="text-crit">*</span></>}
            hint={<>Standard 5-field cron. Example: <span className="mono">0 9 * * *</span> runs daily at 09:00.</>}
          >
            <Input
              id="sub-cron"
              type="text"
              className="mono"
              placeholder="0 9 * * *"
              aria-invalid={!!errors.cronExpression}
              {...register('cronExpression')}
            />
            {errors.cronExpression && <span className="text-xs text-crit">{errors.cronExpression.message}</span>}
          </Field>
        </div>
      ),
    },
    {
      id: 'recipients',
      title: 'Recipients',
      description: 'Where alerts are delivered.',
      fields: ['recipientIds'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label={<>Recipients {!createTasks && <span className="text-crit">*</span>}</>}>
            <SearchMultiSelect<RecipientEntry>
              path="/beacon/api/recipients"
              queryKey={['recipients']}
              selected={selectedRecipients}
              onChange={onRecipientsChange}
              getId={x => x.id}
              getLabel={x => x.name}
              noun="recipients"
              emptyContent="No recipients yet. Add one from the Recipients page first."
              hasError={!!errors.recipientIds}
              renderItem={r => (
                <span className="flex items-center gap-2">
                  <span className="font-medium">{r.name}</span>
                  <span className="text-text-muted mono text-xs truncate">{r.destination}</span>
                  <Pill className="ml-auto">{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</Pill>
                </span>
              )}
            />
            {errors.recipientIds && (
              <span className="text-xs text-crit">{errors.recipientIds.message as string}</span>
            )}
          </Field>

          <Field label="Max rows">
            <Input
              id="sub-max-rows"
              type="number"
              placeholder="No limit"
              {...register('maxRows', {
                setValueAs: v => (v === '' || v == null ? null : Number(v)),
              })}
            />
          </Field>

          <Field label="Timeout (seconds)">
            <Input
              id="sub-timeout"
              type="number"
              placeholder="Default"
              {...register('timeoutSeconds', {
                setValueAs: v => (v === '' || v == null ? null : Number(v)),
              })}
            />
          </Field>

          <div className="grid gap-1.5">
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('includeAttachment')} />
              <span>Include results as attachment</span>
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('showQuery')} />
              <span>Show query text in notification</span>
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('storeResults')} />
              <span>Store result rows for later viewing</span>
            </label>
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                {...register('createTasks', {
                  // Ticking it lifts the recipients requirement, so clear an error already shown.
                  onChange: () => {
                    if (errors.recipientIds) {
                      void form.trigger('recipientIds');
                    }
                  },
                })}
              />
              <span>Create tasks for each result row</span>
            </label>
          </div>
        </div>
      ),
    },
    {
      id: 'review',
      title: 'Review',
      description: 'Confirm and create.',
      render: () => {
        const values = form.getValues();
        return (
          <dl className="grid grid-cols-[140px_1fr] gap-x-3 gap-y-2 text-sm">
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Query</dt>
            <dd><SelectedQueryLabel id={queryId} /></dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Schedule</dt>
            <dd className="mono">{cronExpression}</dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Recipients</dt>
            <dd>
              {selectedRecipients.length === 0
                ? <span className="text-text-muted">None selected</span>
                : (
                  <div className="flex flex-col gap-1">
                    {selectedRecipients.map(r => (
                      <span key={r.id}>
                        {r.name}{' '}
                        <span className="text-text-muted mono text-xs">{r.destination}</span>
                      </span>
                    ))}
                  </div>
                )}
            </dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Max rows</dt>
            <dd>{values.maxRows ?? <span className="text-text-muted">No limit</span>}</dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Timeout</dt>
            <dd>
              {values.timeoutSeconds == null
                ? <span className="text-text-muted">Default</span>
                : <span className="mono">{values.timeoutSeconds}s</span>}
            </dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Options</dt>
            <dd>
              {[
                values.includeAttachment && 'Attachment',
                values.showQuery && 'Show query',
                values.storeResults && 'Store results',
                values.createTasks && 'Create tasks',
              ].filter(Boolean).join(', ') || <span className="text-text-muted">None</span>}
            </dd>
          </dl>
        );
      },
    },
  ];

  return (
    <StepperDialog<FormValues>
      open={open}
      onClose={onClose}
      title="New subscription"
      sub="Schedule a query and route its results to recipients."
      steps={steps}
      form={form}
      onFinish={onFinish}
      busy={createMutation.isPending}
      finishLabel="Create subscription"
    />
  );
}

// ---------------------------------------------------------------------------
// Searchable query picker — server-side search via /beacon/api/queries/.
// ---------------------------------------------------------------------------

interface QueryPickerProps {
  value: number | null;
  onChange: (id: number | null) => void;
  hasError?: boolean;
}

function QueryPicker({ value, onChange, hasError }: QueryPickerProps) {
  const [open, setOpen] = useState(false);
  const [term, setTerm] = useState('');
  const [debounced, setDebounced] = useState('');
  const [highlight, setHighlight] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(term.trim()), 200);
    return () => window.clearTimeout(t);
  }, [term]);

  const list = useQueriesListQuery({ searchTerm: debounced || undefined, pageSize: 20 });
  const selected = useQueryDetailQuery(value ?? undefined);

  useEffect(() => {
    if (!open) return;
    const onDocClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', onDocClick);
    return () => document.removeEventListener('mousedown', onDocClick);
  }, [open]);

  useEffect(() => {
    if (open) requestAnimationFrame(() => inputRef.current?.focus());
  }, [open]);

  const items = list.data?.items ?? [];
  const totalCount = list.data?.totalCount ?? 0;

  const selectedName = value != null && value > 0
    ? selected.data?.name ?? `#${value}`
    : null;

  const pick = (id: number) => {
    onChange(id);
    setOpen(false);
    setTerm('');
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setHighlight(h => Math.min(h + 1, items.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlight(h => Math.max(h - 1, 0));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const item = items[highlight];
      if (item) pick(item.queryId);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      setOpen(false);
    }
  };

  const triggerCls = cn(
    'w-full bg-surface text-text border border-border-strong rounded-sm px-2.5 py-1.5 text-sm',
    'flex items-center gap-2 cursor-pointer text-left justify-between',
    'focus:border-brand-500 focus:outline-none focus:shadow-ring',
    hasError && 'border-crit',
  );

  return (
    <div ref={containerRef} className="relative">
      {!open && (
        <button type="button" className={triggerCls} onClick={() => setOpen(true)}>
          {selectedName
            ? (
              <span className="flex items-center gap-2 min-w-0">
                <span className="font-semibold truncate">{selectedName}</span>
                <span className="text-text-muted mono text-xs">#{value}</span>
              </span>
            )
            : <span className="text-text-muted">Search queries by name…</span>}
          <ChevronRight className="size-3.5 text-text-muted" />
        </button>
      )}

      {open && (
        <>
          <Input
            ref={inputRef}
            type="text"
            value={term}
            placeholder="Type to search queries…"
            onChange={e => { setTerm(e.target.value); setHighlight(0); }}
            onKeyDown={onKeyDown}
            autoComplete="off"
            aria-invalid={hasError}
          />
          <div className="absolute top-[calc(100%+4px)] left-0 right-0 z-10 bg-surface border border-border rounded-sm shadow-pop max-h-72 overflow-auto">
            {list.isLoading && (
              <div className="text-text-muted p-3 text-sm">Searching…</div>
            )}
            {!list.isLoading && items.length === 0 && (
              <div className="text-text-muted p-3 text-sm">
                {debounced ? `No queries match "${debounced}".` : 'No queries yet.'}
              </div>
            )}
            {!list.isLoading && items.map((q, i) => (
              <button
                type="button"
                key={q.queryId}
                onClick={() => pick(q.queryId)}
                onMouseEnter={() => setHighlight(i)}
                className={cn(
                  'flex w-full items-center gap-2 px-3 py-2 border-0 text-left text-sm cursor-pointer',
                  i === highlight ? 'bg-surface-2' : 'bg-transparent',
                )}
              >
                <div className="flex-1 min-w-0">
                  <div className="font-semibold truncate">{q.name}</div>
                  {q.description && (
                    <div className="text-text-muted text-xs truncate">{q.description}</div>
                  )}
                </div>
                <span className="text-text-muted mono text-xs">#{q.queryId}</span>
                {q.subscriptionsCount > 0 && (
                  <Pill>{q.subscriptionsCount} sub{q.subscriptionsCount === 1 ? '' : 's'}</Pill>
                )}
              </button>
            ))}
            {!list.isLoading && totalCount > items.length && (
              <div className="text-text-muted px-3 py-1.5 text-xs border-t border-border">
                Showing {items.length} of {totalCount}. Refine your search to narrow.
              </div>
            )}
          </div>
        </>
      )}
    </div>
  );
}

function SelectedQueryLabel({ id }: { id: number }) {
  const detail = useQueryDetailQuery(id > 0 ? id : undefined);
  if (id <= 0) return <span className="text-text-muted">None selected</span>;
  return (
    <>
      <span className="font-semibold">{detail.data?.name ?? `#${id}`}</span>
      <span className="text-text-muted mono ml-1.5 text-xs">#{id}</span>
    </>
  );
}
