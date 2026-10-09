import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { ChevronRight, Info } from 'lucide-react';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { Field, Input, Pill, Select } from '@/components/beacon';
import { cn } from '@/lib/cn';
import { SearchMultiSelect } from '@/components/data/SearchMultiSelect';
import {
  AnomalyDetectionMethod,
  AnomalySensitivity,
  FileType,
  NotificationTrigger,
  ParameterType,
} from '@/lib/enums';
import { NOTIFICATION_TYPE_LABEL, type RecipientEntry } from '@/routes/recipients/queries';
import { useQueriesListQuery, useQueryDetailQuery, type QueryStepParameter } from '@/routes/queries/queries';
import {
  ANOMALY_DETECTION_METHOD_LABEL,
  ANOMALY_SENSITIVITY_LABEL,
  FILE_TYPE_LABEL,
  NOTIFICATION_TRIGGER_LABEL,
  useCreateSubscription,
} from './queries';

const optionalCount = z.number().int().min(0).nullable().optional();

const SCHEMA = z.object({
  queryId: z.number({ message: 'Query id is required' }).int().min(1, 'Query id is required'),
  cronExpression: z.string().trim().min(1, 'Cron expression is required').max(200),
  // One value per query placeholder — the server rejects a subscription that leaves one unbound.
  parameters: z.array(z.object({
    queryPlaceholder: z.string(),
    value: z.string().trim().min(1, 'Required'),
  })),
  // The query `parameters` was built for, so Next waits until the picked query's placeholders have loaded.
  parametersQueryId: z.number().int(),
  notificationTrigger: z.number().int(),
  minimumRowCount: optionalCount,
  recipientIds: z.array(z.number().int()),
  maxRows: optionalCount,
  timeoutSeconds: optionalCount,
  includeAttachment: z.boolean(),
  resultAttachmentType: z.number().int(),
  showQuery: z.boolean(),
  storeResults: z.boolean(),
  createTasks: z.boolean(),
  anomalyEnabled: z.boolean(),
  anomaly: z.object({
    detectionMethod: z.number().int(),
    sensitivity: z.number().int(),
    lookbackDays: z.number({ message: 'Required' }).int().min(7, 'At least 7 days').max(365, 'At most 365 days'),
    minimumDataPoints: z.number({ message: 'Required' }).int().min(3, 'At least 3').max(100, 'At most 100'),
    alertOnIncrease: z.boolean(),
    alertOnDecrease: z.boolean(),
  })
    .refine(x => x.alertOnIncrease || x.alertOnDecrease, {
      message: 'Alert on an increase, a decrease, or both',
      path: ['alertOnIncrease'],
    }),
})
  // Mirrors SubscriptionService: a task-tracking subscription needs no one to notify.
  .refine(x => x.createTasks || x.recipientIds.length > 0, {
    message: 'Pick at least one recipient, or create a task instead',
    path: ['recipientIds'],
  })
  .refine(x => x.queryId <= 0 || x.parametersQueryId === x.queryId, {
    message: "Loading the query's parameters…",
    path: ['parameters'],
  });

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  queryId: 0,
  cronExpression: '0 9 * * *',
  parameters: [],
  parametersQueryId: 0,
  notificationTrigger: NotificationTrigger.OnResultCountChange,
  minimumRowCount: null,
  recipientIds: [],
  maxRows: null,
  timeoutSeconds: null,
  includeAttachment: false,
  resultAttachmentType: FileType.Csv,
  showQuery: false,
  storeResults: false,
  createTasks: false,
  anomalyEnabled: false,
  anomaly: {
    detectionMethod: AnomalyDetectionMethod.StandardDeviation,
    sensitivity: AnomalySensitivity.Medium,
    lookbackDays: 30,
    minimumDataPoints: 7,
    alertOnIncrease: true,
    alertOnDecrease: true,
  },
};

const TRIGGER_OPTIONS = [
  NotificationTrigger.OnResultCountChange,
  NotificationTrigger.Always,
  NotificationTrigger.OnResultCountIncrease,
];

const METHOD_HINT: Record<number, string> = {
  [AnomalyDetectionMethod.StandardDeviation]:
    'Flags runs whose row count sits too many standard deviations from the historical average. Suits steady data.',
  [AnomalyDetectionMethod.IQR]:
    'Treats the middle 50% of past runs as normal. Robust when the history has occasional spikes.',
  [AnomalyDetectionMethod.PercentageChange]:
    'Flags runs whose row count moves by more than a set percentage from the historical average.',
};

// Thresholds mirror AnomalyDetectionService.
function sensitivityHint(method: number, sensitivity: number) {
  if (method === AnomalyDetectionMethod.IQR) {
    return 'IQR always flags values beyond 1.5 × IQR; sensitivity does not change it.';
  }
  if (method === AnomalyDetectionMethod.PercentageChange) {
    const pct = { [AnomalySensitivity.High]: '15%', [AnomalySensitivity.Medium]: '25%', [AnomalySensitivity.Low]: '40%' }[sensitivity];
    return `Alerts when the row count changes by ${pct} or more.`;
  }
  const sigma = { [AnomalySensitivity.High]: '1.5σ', [AnomalySensitivity.Medium]: '2σ', [AnomalySensitivity.Low]: '3σ' }[sensitivity];
  return `Alerts when the row count is ${sigma} or more from the average.`;
}

const PARAMETER_INPUT_TYPE: Record<number, string> = {
  [ParameterType.Number]: 'number',
  [ParameterType.DateTime]: 'datetime-local',
  [ParameterType.String]: 'text',
};

const nullableNumber = (v: unknown) => (v === '' || v == null ? null : Number(v));

const LABEL_CLS = 'text-2xs font-semibold uppercase tracking-eyebrow text-text-muted';

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
  const { register, watch, setValue, getValues, reset, formState: { errors } } = form;

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
  const includeAttachment = watch('includeAttachment');
  const anomalyEnabled = watch('anomalyEnabled');
  const anomalyMethod = watch('anomaly.detectionMethod');
  const anomalySensitivity = watch('anomaly.sensitivity');

  const queryDetail = useQueryDetailQuery(queryId > 0 ? queryId : undefined);

  // One input per distinct placeholder — the server binds each query placeholder exactly once.
  const parameterFields = useMemo(() => {
    const seen = new Set<string>();
    const result: QueryStepParameter[] = [];
    for (const p of (queryDetail.data?.steps ?? []).flatMap(x => x.parameters)) {
      if (p.placeholder && !seen.has(p.placeholder)) {
        seen.add(p.placeholder);
        result.push(p);
      }
    }
    return result;
  }, [queryDetail.data]);

  useEffect(() => {
    if (!open || !queryDetail.data) return;
    const current = getValues('parametersQueryId') === queryId ? getValues('parameters') : [];
    setValue('parameters', parameterFields.map(x => ({
      queryPlaceholder: x.placeholder!,
      value: current.find(y => y.queryPlaceholder === x.placeholder)?.value ?? '',
    })));
    setValue('parametersQueryId', queryId);
  }, [open, queryId, queryDetail.data, parameterFields, getValues, setValue]);

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
        notificationTrigger: values.notificationTrigger as NotificationTrigger,
        minimumRowCount: values.minimumRowCount ?? null,
        // The job attaches a file only when a format is set, so the format rides on the checkbox.
        resultAttachmentType: values.includeAttachment ? values.resultAttachmentType as FileType : null,
        parameters: values.parameters.map(x => ({ queryPlaceholder: x.queryPlaceholder, value: x.value.trim() })),
        anomalyConfig: values.anomalyEnabled
          ? {
            ...values.anomaly,
            detectionMethod: values.anomaly.detectionMethod as AnomalyDetectionMethod,
            sensitivity: values.anomaly.sensitivity as AnomalySensitivity,
          }
          : null,
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
      fields: ['queryId', 'cronExpression', 'parameters'],
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

          {parameterFields.length > 0 && (
            <div className="flex flex-col gap-3 border-t border-border pt-3.5">
              <div>
                <div className="text-sm font-semibold">Query parameters</div>
                <div className="text-xs text-text-muted">Every scheduled run uses these values.</div>
              </div>
              {parameterFields.map((p, i) => (
                <Field
                  key={p.placeholder}
                  label={<>{p.name} <span className="text-crit">*</span></>}
                  hint={<>{p.description && <>{p.description} · </>}<span className="mono">{p.placeholder}</span></>}
                >
                  <Input
                    id={`sub-param-${i}`}
                    type={PARAMETER_INPUT_TYPE[p.type] ?? 'text'}
                    aria-invalid={!!errors.parameters?.[i]?.value}
                    {...register(`parameters.${i}.value`)}
                  />
                  {errors.parameters?.[i]?.value && (
                    <span className="text-xs text-crit">{errors.parameters[i]?.value?.message}</span>
                  )}
                </Field>
              ))}
            </div>
          )}
          {queryDetail.isError && (
            <span className="text-xs text-crit">Couldn't load this query's parameters.</span>
          )}
          {!queryDetail.isError && errors.parameters?.message && (
            <span className="text-xs text-text-muted">{errors.parameters.message}</span>
          )}
        </div>
      ),
    },
    {
      id: 'notify',
      title: 'Notify',
      description: 'When and who to alert.',
      fields: ['notificationTrigger', 'minimumRowCount', 'recipientIds'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label="Send notification" hint="Which runs notify the recipients.">
            <Select id="sub-trigger" {...register('notificationTrigger', { valueAsNumber: true })}>
              {TRIGGER_OPTIONS.map(x => (
                <option key={x} value={x}>{NOTIFICATION_TRIGGER_LABEL[x].description}</option>
              ))}
            </Select>
          </Field>

          <Field label="Minimum row count" hint="Only notify when a run returns at least this many rows.">
            <Input
              id="sub-min-rows"
              type="number"
              placeholder="No threshold"
              {...register('minimumRowCount', { setValueAs: nullableNumber })}
            />
          </Field>

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
                  {r.description && <span className="text-text-muted text-xs truncate">{r.description}</span>}
                  <Pill className="ml-auto">{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</Pill>
                </span>
              )}
            />
            {errors.recipientIds && (
              <span className="text-xs text-crit">{errors.recipientIds.message as string}</span>
            )}
          </Field>

          <div className="flex items-center gap-1.5">
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
              <span>Create a task and keep it open while the query returns rows</span>
            </label>
            {/* Mirrors TaskService.CreateOrUpdateTask, called by JobService on every run. */}
            <InfoTip>
              The first run that returns rows creates a task for this subscription on the Tasks page and
              keeps it open. Each later run updates the task's row count. A run that returns no rows resolves
              the task automatically, and the next run with rows creates a new one. This happens on every
              run, whatever the notification setting, so recipients become optional.
            </InfoTip>
          </div>
        </div>
      ),
    },
    {
      id: 'results',
      title: 'Results',
      description: 'What each notification carries.',
      fields: ['maxRows', 'timeoutSeconds'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <div className="grid grid-cols-2 gap-3.5">
            <Field label="Max rows" hint="Rows shown in the notification.">
              <Input
                id="sub-max-rows"
                type="number"
                placeholder="No limit"
                {...register('maxRows', { setValueAs: nullableNumber })}
              />
            </Field>

            <Field label="Timeout (seconds)" hint="Longest a run may take.">
              <Input
                id="sub-timeout"
                type="number"
                placeholder="No timeout"
                {...register('timeoutSeconds', { setValueAs: nullableNumber })}
              />
            </Field>
          </div>

          <div className="grid gap-1.5">
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('includeAttachment')} />
              <span>Include results as attachment</span>
            </label>
            {includeAttachment && (
              <Field label="Attachment format" className="ml-6 max-w-[220px]">
                <Select id="sub-attachment-type" {...register('resultAttachmentType', { valueAsNumber: true })}>
                  {[FileType.Csv, FileType.Xlsx].map(x => (
                    <option key={x} value={x}>{FILE_TYPE_LABEL[x]}</option>
                  ))}
                </Select>
              </Field>
            )}
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('showQuery')} />
              <span>Show query text in notification</span>
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('storeResults')} />
              <span>Store result rows for later viewing</span>
            </label>
          </div>
        </div>
      ),
    },
    {
      id: 'anomaly',
      title: 'Anomaly',
      description: 'Alert on unusual result counts.',
      fields: anomalyEnabled ? ['anomaly'] : [],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <p className="text-xs text-text-muted">
            Anomaly detection learns the normal row count from past runs and alerts when a run deviates
            significantly from that baseline.
          </p>

          <label className="flex items-center gap-2">
            <input type="checkbox" {...register('anomalyEnabled')} />
            <span>Enable anomaly detection</span>
          </label>

          {anomalyEnabled && (
            <>
              <Field label="Detection method" hint={METHOD_HINT[anomalyMethod]}>
                <Select id="sub-anomaly-method" {...register('anomaly.detectionMethod', { valueAsNumber: true })}>
                  {Object.entries(ANOMALY_DETECTION_METHOD_LABEL).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </Select>
              </Field>

              <Field label="Sensitivity" hint={sensitivityHint(anomalyMethod, anomalySensitivity)}>
                <Select id="sub-anomaly-sensitivity" {...register('anomaly.sensitivity', { valueAsNumber: true })}>
                  {Object.entries(ANOMALY_SENSITIVITY_LABEL).map(([value, label]) => (
                    <option key={value} value={value}>{label}</option>
                  ))}
                </Select>
              </Field>

              <div className="grid grid-cols-2 gap-3.5">
                <Field label="Lookback (days)" hint="History used for the baseline, 7–365.">
                  <Input
                    id="sub-anomaly-lookback"
                    type="number"
                    aria-invalid={!!errors.anomaly?.lookbackDays}
                    {...register('anomaly.lookbackDays', { valueAsNumber: true })}
                  />
                  {errors.anomaly?.lookbackDays && (
                    <span className="text-xs text-crit">{errors.anomaly.lookbackDays.message}</span>
                  )}
                </Field>

                <Field label="Minimum data points" hint="Runs needed before alerting, 3–100.">
                  <Input
                    id="sub-anomaly-min-points"
                    type="number"
                    aria-invalid={!!errors.anomaly?.minimumDataPoints}
                    {...register('anomaly.minimumDataPoints', { valueAsNumber: true })}
                  />
                  {errors.anomaly?.minimumDataPoints && (
                    <span className="text-xs text-crit">{errors.anomaly.minimumDataPoints.message}</span>
                  )}
                </Field>
              </div>

              <div className="grid gap-1.5">
                <label className="flex items-center gap-2">
                  <input type="checkbox" {...register('anomaly.alertOnIncrease')} />
                  <span>Alert on an unusual increase</span>
                </label>
                <label className="flex items-center gap-2">
                  <input type="checkbox" {...register('anomaly.alertOnDecrease')} />
                  <span>Alert on an unusual decrease</span>
                </label>
                {errors.anomaly?.alertOnIncrease && (
                  <span className="text-xs text-crit">{errors.anomaly.alertOnIncrease.message}</span>
                )}
              </div>
            </>
          )}
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
            <dt className={LABEL_CLS}>Query</dt>
            <dd><SelectedQueryLabel id={queryId} /></dd>
            <dt className={LABEL_CLS}>Schedule</dt>
            <dd className="mono">{cronExpression}</dd>
            {values.parameters.length > 0 && (
              <>
                <dt className={LABEL_CLS}>Parameters</dt>
                <dd className="flex flex-col gap-1">
                  {values.parameters.map(x => (
                    <span key={x.queryPlaceholder}>
                      <span className="mono text-xs text-text-muted">{x.queryPlaceholder}</span>{' '}
                      <span className="mono">{x.value}</span>
                    </span>
                  ))}
                </dd>
              </>
            )}
            <dt className={LABEL_CLS}>Notify</dt>
            <dd>
              {NOTIFICATION_TRIGGER_LABEL[values.notificationTrigger]?.description}
              {values.minimumRowCount != null && (
                <span className="text-text-muted">, at ≥ {values.minimumRowCount} rows</span>
              )}
            </dd>
            <dt className={LABEL_CLS}>Recipients</dt>
            <dd>
              {selectedRecipients.length === 0
                ? <span className="text-text-muted">None selected</span>
                : (
                  <div className="flex flex-col gap-1">
                    {selectedRecipients.map(r => (
                      <span key={r.id}>
                        {r.name}{' '}
                        <span className="text-text-muted text-xs">{NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}</span>
                      </span>
                    ))}
                  </div>
                )}
            </dd>
            <dt className={LABEL_CLS}>Max rows</dt>
            <dd>{values.maxRows ?? <span className="text-text-muted">No limit</span>}</dd>
            <dt className={LABEL_CLS}>Timeout</dt>
            <dd>
              {values.timeoutSeconds == null
                ? <span className="text-text-muted">No timeout</span>
                : <span className="mono">{values.timeoutSeconds}s</span>}
            </dd>
            <dt className={LABEL_CLS}>Options</dt>
            <dd>
              {[
                values.includeAttachment && `Attachment (${FILE_TYPE_LABEL[values.resultAttachmentType]})`,
                values.showQuery && 'Show query',
                values.storeResults && 'Store results',
                values.createTasks && 'Create task',
              ].filter(Boolean).join(', ') || <span className="text-text-muted">None</span>}
            </dd>
            <dt className={LABEL_CLS}>Anomaly</dt>
            <dd>
              {values.anomalyEnabled
                ? (
                  <>
                    {ANOMALY_DETECTION_METHOD_LABEL[values.anomaly.detectionMethod]},{' '}
                    {ANOMALY_SENSITIVITY_LABEL[values.anomaly.sensitivity]?.toLowerCase()} sensitivity,{' '}
                    {values.anomaly.lookbackDays}-day lookback
                  </>
                )
                : <span className="text-text-muted">Off</span>}
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
      size="xl"
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

/** Info icon that shows a longer explanation on hover or keyboard focus. */
function InfoTip({ children }: { children: ReactNode }) {
  const id = useId();
  return (
    <span className="group relative inline-flex">
      <button
        type="button"
        aria-label="More info"
        aria-describedby={id}
        className="inline-flex cursor-help border-0 bg-transparent p-0.5 text-text-muted hover:text-text focus:outline-none focus-visible:text-brand-600"
      >
        <Info className="size-3.5" />
      </button>
      <span
        id={id}
        role="tooltip"
        className={cn(
          'pointer-events-none invisible absolute bottom-[calc(100%+6px)] left-1/2 z-20 w-80 -translate-x-1/2',
          'rounded-sm border border-border bg-surface p-2.5 text-xs leading-relaxed text-text shadow-pop',
          'opacity-0 transition-opacity group-hover:visible group-hover:opacity-100',
          'group-focus-within:visible group-focus-within:opacity-100',
        )}
      >
        {children}
      </span>
    </span>
  );
}
