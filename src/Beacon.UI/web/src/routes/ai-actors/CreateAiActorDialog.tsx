import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { StepperDialog, type StepperDialogStep } from '@/components/ui/StepperDialog';
import { Field, Input, Select, Textarea } from '@/components/beacon';
import { useDataSourcesQuery } from '@/routes/data-sources/queries';
import { useCreateAiActor } from './queries';

const SCHEMA = z.object({
  name: z.string().trim().min(1, 'Name is required').max(200),
  instructions: z.string().trim().min(1, 'Instructions are required'),
  dataSourceId: z.number({ message: 'Pick a data source' }).int().min(1, 'Pick a data source'),
  additionalContext: z.string(),
  maxQueries: z.number().int().min(1).nullable(),
  activateImmediately: z.boolean(),
});

type FormValues = z.infer<typeof SCHEMA>;

const DEFAULTS: FormValues = {
  name: '',
  instructions: '',
  dataSourceId: 0,
  additionalContext: '',
  maxQueries: null,
  activateImmediately: false,
};

interface CreateAiActorDialogProps {
  open: boolean;
  onClose: () => void;
  initialDataSourceId?: number;
}

export function CreateAiActorDialog({ open, onClose, initialDataSourceId }: CreateAiActorDialogProps) {
  const dsQuery = useDataSourcesQuery();
  const createMutation = useCreateAiActor();

  const form = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { ...DEFAULTS, dataSourceId: initialDataSourceId ?? 0 },
    mode: 'onTouched',
  });
  const { register, reset, watch, formState: { errors } } = form;

  useEffect(() => {
    if (!open) return;
    reset({ ...DEFAULTS, dataSourceId: initialDataSourceId ?? 0 });
  }, [open, reset, initialDataSourceId]);

  const dataSourceId = watch('dataSourceId');

  const dataSources = dsQuery.data?.entries ?? [];

  const onFinish = async () => {
    const v = form.getValues();
    try {
      await createMutation.mutateAsync({
        name: v.name.trim(),
        instructions: v.instructions.trim(),
        dataSourceId: v.dataSourceId,
        additionalContext: v.additionalContext.trim() || null,
        maxQueries: v.maxQueries,
        maxSubscriptionsPerQuery: null,
        createdByUserId: null,
        defaultRecipientIds: null,
        activateImmediately: v.activateImmediately,
      });
      onClose();
    } catch {
      // createSimpleMutation already surfaced the error toast — keep the
      // dialog open so the user can retry.
    }
  };

  const steps: StepperDialogStep<FormValues>[] = [
    {
      id: 'basics',
      title: 'Basics',
      description: 'Name and data source.',
      fields: ['name', 'dataSourceId'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field label={<>Name <span className="text-crit">*</span></>}>
            <Input
              id="ca-name"
              type="text"
              placeholder="Revenue monitor"
              aria-invalid={!!errors.name}
              {...register('name')}
            />
            {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
          </Field>

          <Field
            label={<>Data source <span className="text-crit">*</span></>}
            hint={dsQuery.isLoading ? 'Loading data sources…' : undefined}
          >
            <Select
              id="ca-ds"
              aria-invalid={!!errors.dataSourceId}
              {...register('dataSourceId', { valueAsNumber: true })}
            >
              <option value={0}>— Select data source —</option>
              {dataSources.map(ds => (
                <option key={ds.id} value={ds.id}>{ds.name}</option>
              ))}
            </Select>
            {errors.dataSourceId && <span className="text-xs text-crit">{errors.dataSourceId.message}</span>}
          </Field>
        </div>
      ),
    },
    {
      id: 'instructions',
      title: 'Instructions',
      description: 'What should the actor do?',
      fields: ['instructions'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field
            label={<>Instructions <span className="text-crit">*</span></>}
            hint="Describe what queries the actor should manage and what conditions to monitor."
          >
            <Textarea
              id="ca-inst"
              rows={8}
              placeholder="Monitor sales metrics daily. Alert when revenue drops below 10% of the weekly average. Focus on the revenue and orders tables."
              aria-invalid={!!errors.instructions}
              {...register('instructions')}
            />
            {errors.instructions && <span className="text-xs text-crit">{errors.instructions.message}</span>}
          </Field>

          <Field label="Additional context">
            <Textarea
              id="ca-ctx"
              rows={3}
              placeholder="Optional schema hints, business context, or constraints."
              {...register('additionalContext')}
            />
          </Field>
        </div>
      ),
    },
    {
      id: 'options',
      title: 'Options',
      description: 'Limits and activation.',
      fields: ['maxQueries', 'activateImmediately'],
      render: () => (
        <div className="flex flex-col gap-3.5">
          <Field
            label="Max queries (optional)"
            hint="Maximum number of queries this actor can create. Leave blank for no limit."
          >
            <Input
              id="ca-max"
              type="number"
              placeholder="No limit"
              min={1}
              {...register('maxQueries', {
                setValueAs: v => (v === '' || v == null ? null : Number(v)),
              })}
            />
          </Field>

          <div>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('activateImmediately')} />
              <span className="text-sm">Activate immediately</span>
            </label>
            <div className="text-xs text-text-subtle mt-1">
              If unchecked, the actor starts in Draft status and must be activated manually.
            </div>
          </div>
        </div>
      ),
    },
    {
      id: 'review',
      title: 'Review',
      description: 'Confirm and create.',
      render: () => {
        const v = form.getValues();
        const dsName = dataSources.find(d => d.id === dataSourceId)?.name ?? '—';
        return (
          <dl className="grid grid-cols-[140px_1fr] gap-x-3 gap-y-2 text-sm m-0">
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted self-center">Name</dt>
            <dd>{v.name || <span className="text-text-muted">(unset)</span>}</dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted self-center">Data source</dt>
            <dd>{dsName}</dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted self-center">Max queries</dt>
            <dd>{v.maxQueries ?? <span className="text-text-muted">No limit</span>}</dd>
            <dt className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted self-center">Activation</dt>
            <dd>{v.activateImmediately ? 'Active immediately' : 'Draft'}</dd>
          </dl>
        );
      },
    },
  ];

  return (
    <StepperDialog<FormValues>
      open={open}
      onClose={onClose}
      title="New AI actor"
      sub="Create an actor that autonomously manages queries for a data source."
      size="md"
      steps={steps}
      form={form}
      onFinish={onFinish}
      busy={createMutation.isPending}
      finishLabel="Create actor"
    />
  );
}
