import { useEffect, useMemo } from 'react';
import { useFieldArray, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Dialog } from '@/components/ui/Dialog';
import {
  Button,
  Card,
  Field,
  Input,
  Select,
  Textarea,
} from '@/components/beacon';
import { useDataSourcesQuery } from '../data-sources/queries';
import { useRecipientsQuery } from '../recipients/queries';
import { DataContractRuleType, DataContractSeverity } from '@/lib/enums';
import {
  useCreateContract,
  useDataContract,
  useUpdateContract,
  type CreateContractPayload,
  type DataContractRuleData,
} from './queries';

interface Props {
  editContractId: number | null;
  onClose: (success: boolean) => void;
}

const RULE_TYPE_LABEL: Record<number, string> = {
  [DataContractRuleType.Volume]: 'Volume',
  [DataContractRuleType.Freshness]: 'Freshness',
  [DataContractRuleType.NullRate]: 'Null rate',
  [DataContractRuleType.Uniqueness]: 'Uniqueness',
  [DataContractRuleType.Referential]: 'Referential',
  [DataContractRuleType.Range]: 'Range',
  [DataContractRuleType.Pattern]: 'Pattern',
  [DataContractRuleType.CustomSql]: 'Custom SQL',
};

const SEVERITY_LABEL: Record<number, string> = {
  [DataContractSeverity.Low]: 'Low',
  [DataContractSeverity.Medium]: 'Medium',
  [DataContractSeverity.High]: 'High',
  [DataContractSeverity.Critical]: 'Critical',
};

const CONFIG_HINT: Record<number, string> = {
  [DataContractRuleType.Volume]: '{"minRows": 100}',
  [DataContractRuleType.Freshness]: '{"column": "updated_at", "maxAgeMinutes": 60}',
  [DataContractRuleType.NullRate]: '{"column": "email", "maxNullPercent": 5}',
  [DataContractRuleType.Uniqueness]: '{"column": "id"}',
  [DataContractRuleType.Referential]: '{"column": "user_id", "referenceTable": "users", "referenceColumn": "id"}',
  [DataContractRuleType.Range]: '{"column": "age", "min": "0", "max": "150"}',
  [DataContractRuleType.Pattern]: '{"column": "email", "pattern": "^[^@]+@[^@]+$"}',
  [DataContractRuleType.CustomSql]: '{"sql": "SELECT CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS passed FROM ..."}',
};

const REQ = <span className="text-crit">*</span>;

const ruleSchema = z.object({
  id: z.number().optional(),
  name: z.string().trim().min(1, 'Required').max(200),
  description: z.string().max(1000),
  ruleType: z.number(),
  columnName: z.string(),
  configuration: z.string(),
  severity: z.number(),
  weight: z.number().min(0.1).max(10),
  isEnabled: z.boolean(),
});

const schema = z.object({
  name: z.string().trim().min(1, 'Required').max(200),
  description: z.string().max(1000),
  dataSourceId: z.number(),
  schemaName: z.string().trim().min(1, 'Required').max(200),
  tableName: z.string().trim().min(1, 'Required').max(200),
  cronExpression: z.string().trim().min(1, 'Required').max(100),
  isEnabled: z.boolean(),
  alertOnFailure: z.boolean(),
  failureThreshold: z.number().min(0).max(100),
  rules: z.array(ruleSchema),
  recipientIds: z.array(z.number()),
});

type FormValues = z.infer<typeof schema>;

const DEFAULTS: FormValues = {
  name: '',
  description: '',
  dataSourceId: undefined as unknown as number,
  schemaName: '',
  tableName: '',
  cronExpression: '0 */6 * * *',
  isEnabled: true,
  alertOnFailure: true,
  failureThreshold: 80,
  rules: [],
  recipientIds: [],
};

function blankRule(index: number): FormValues['rules'][number] {
  return {
    name: `Rule ${index + 1}`,
    description: '',
    ruleType: DataContractRuleType.Volume,
    columnName: '',
    configuration: '{}',
    severity: DataContractSeverity.Medium,
    weight: 1,
    isEnabled: true,
  };
}

export function CreateDataContractDialog({ editContractId, onClose }: Props) {
  const isEdit = editContractId !== null;
  const dataSources = useDataSourcesQuery();
  const recipientsQ = useRecipientsQuery();
  const existingQ = useDataContract(editContractId);
  const createMutation = useCreateContract();
  const updateMutation = useUpdateContract(editContractId ?? 0);

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: DEFAULTS,
    mode: 'onChange',
  });

  const { register, control, handleSubmit, reset, watch, setValue, formState } = form;
  const { isValid } = formState;
  const ruleArray = useFieldArray({ control, name: 'rules' });
  const alertOnFailure = watch('alertOnFailure');
  const rules = watch('rules');

  useEffect(() => {
    const c = existingQ.data;
    if (!c) return;
    reset({
      name: c.name,
      description: c.description ?? '',
      dataSourceId: c.dataSourceId,
      schemaName: c.schemaName,
      tableName: c.tableName,
      cronExpression: c.cronExpression,
      isEnabled: c.isEnabled,
      alertOnFailure: c.alertOnFailure,
      failureThreshold: c.failureThresholdScore,
      recipientIds: c.recipients.map(r => r.id),
      rules: c.rules.map(r => ({
        id: r.id,
        name: r.name,
        description: r.description ?? '',
        ruleType: r.ruleType,
        columnName: r.columnName ?? '',
        configuration: r.configuration,
        severity: r.severity,
        weight: r.weight,
        isEnabled: r.isEnabled,
      })),
    });
  }, [existingQ.data, reset]);

  const submitting = createMutation.isPending || updateMutation.isPending;

  // In edit mode the form must not render on blank DEFAULTS — submitting an
  // unhydrated form would wipe the contract's rules and recipients. Gate on
  // the detail query: spinner while pending, retry on failure, form only
  // once data has arrived.
  const editLoading = isEdit && existingQ.data == null && !existingQ.isError;
  const editFailed = isEdit && existingQ.data == null && existingQ.isError;
  const formReady = !isEdit || existingQ.data != null;

  const recipientOptions = useMemo(
    () => recipientsQ.data?.entries ?? [],
    [recipientsQ.data],
  );

  const onSubmit = (values: FormValues) => {
    const rulesData: DataContractRuleData[] = values.rules.map(r => ({
      id: r.id,
      name: r.name,
      description: r.description || null,
      ruleType: r.ruleType,
      columnName: r.columnName || null,
      configuration: r.configuration || '{}',
      severity: r.severity,
      weight: r.weight,
      isEnabled: r.isEnabled,
    }));

    const payload: CreateContractPayload = {
      dataSourceId: values.dataSourceId,
      schemaName: values.schemaName,
      tableName: values.tableName,
      name: values.name,
      description: values.description || null,
      cronExpression: values.cronExpression,
      isEnabled: values.isEnabled,
      ownerUserId: null,
      alertOnFailure: values.alertOnFailure,
      failureThresholdScore: values.failureThreshold,
      rules: rulesData,
      recipientIds: values.alertOnFailure ? values.recipientIds : null,
    };

    // No onError toast — useCreate/UpdateContract (createSimpleMutation) already toast.
    if (isEdit) {
      updateMutation.mutate(payload, {
        onSuccess: () => onClose(true),
      });
    } else {
      createMutation.mutate(payload, {
        onSuccess: () => onClose(true),
      });
    }
  };

  return (
    <Dialog
      open={true}
      onClose={() => onClose(false)}
      title={isEdit ? 'Edit data contract' : 'New data contract'}
      size="lg"
      footer={
        <>
          <Button type="button" onClick={() => onClose(false)} disabled={submitting}>
            Cancel
          </Button>
          <Button
            variant="primary"
            type="submit"
            form="data-contract-form"
            disabled={!isValid || submitting || !formReady}
          >
            {submitting ? 'Saving…' : isEdit ? 'Update' : 'Create'}
          </Button>
        </>
      }
    >
      {editLoading && (
        <div className="text-text-muted py-6 text-center">Loading contract…</div>
      )}

      {editFailed && (
        <div className="flex flex-col items-start gap-3 py-4">
          <span className="text-sm text-crit">
            Failed to load the contract
            {existingQ.error instanceof Error ? ` — ${existingQ.error.message}` : ''}.
          </span>
          <Button type="button" onClick={() => existingQ.refetch()}>
            Retry
          </Button>
        </div>
      )}

      {formReady && (
      <form id="data-contract-form" onSubmit={handleSubmit(onSubmit)}>
        <div className="flex flex-col gap-3">
          <Field label={<>Contract name {REQ}</>}>
            <Input {...register('name')} maxLength={200} required />
          </Field>

          <Field label="Description">
            <Textarea rows={2} {...register('description')} maxLength={1000} />
          </Field>

          <Field label={<>Data source {REQ}</>}>
            <Select
              {...register('dataSourceId', { valueAsNumber: true })}
              required
              defaultValue=""
            >
              <option value="">Select…</option>
              {(dataSources.data?.entries ?? []).map(d => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </Select>
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field label={<>Schema name {REQ}</>}>
              <Input {...register('schemaName')} maxLength={200} required />
            </Field>
            <Field label={<>Table name {REQ}</>}>
              <Input {...register('tableName')} maxLength={200} required />
            </Field>
          </div>

          <div className="grid grid-cols-[2fr_1fr] gap-3">
            <Field label={<>Cron expression {REQ}</>} hint="e.g. 0 */6 * * * (every 6 hours)">
              <Input {...register('cronExpression')} maxLength={100} required />
            </Field>
            <Field label="Failure threshold (%)">
              <Input
                type="number"
                min={0}
                max={100}
                {...register('failureThreshold', { valueAsNumber: true })}
              />
            </Field>
          </div>

          <div className="flex gap-4 text-sm">
            <label className="flex items-center gap-1.5">
              <input type="checkbox" {...register('isEnabled')} />
              Enabled
            </label>
            <label className="flex items-center gap-1.5">
              <input type="checkbox" {...register('alertOnFailure')} />
              Alert on failure
            </label>
          </div>

          {alertOnFailure && (
            <Field label="Notification recipients">
              <Select
                multiple
                size={4}
                value={watch('recipientIds').map(String)}
                onChange={e => {
                  const opts = Array.from(e.target.selectedOptions).map(o => Number(o.value));
                  setValue('recipientIds', opts, { shouldDirty: true });
                }}
              >
                {recipientOptions.map(r => (
                  <option key={r.id} value={r.id}>{r.name}</option>
                ))}
              </Select>
            </Field>
          )}

          <hr className="border-0 border-t border-border" />

          <div className="flex justify-between items-center">
            <strong>Rules ({ruleArray.fields.length})</strong>
            <Button
              type="button"
              size="sm"
              onClick={() => ruleArray.append(blankRule(ruleArray.fields.length))}
            >
              Add rule
            </Button>
          </div>

          {ruleArray.fields.map((field, index) => (
            <Card key={field.id} className="p-3">
              <div className="flex justify-between mb-2">
                <strong>Rule {index + 1}</strong>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => ruleArray.remove(index)}
                >
                  Remove
                </Button>
              </div>
              <div className="grid grid-cols-2 gap-2">
                <Field label={<>Name {REQ}</>}>
                  <Input {...register(`rules.${index}.name`)} required />
                </Field>
                <Field label="Type">
                  <Select {...register(`rules.${index}.ruleType`, { valueAsNumber: true })}>
                    {Object.entries(RULE_TYPE_LABEL).map(([k, label]) => (
                      <option key={k} value={k}>{label}</option>
                    ))}
                  </Select>
                </Field>
              </div>
              <div className="grid grid-cols-3 gap-2 mt-2">
                <Field label="Column (optional)">
                  <Input {...register(`rules.${index}.columnName`)} />
                </Field>
                <Field label="Severity">
                  <Select {...register(`rules.${index}.severity`, { valueAsNumber: true })}>
                    {Object.entries(SEVERITY_LABEL).map(([k, label]) => (
                      <option key={k} value={k}>{label}</option>
                    ))}
                  </Select>
                </Field>
                <Field label="Weight">
                  <Input
                    type="number"
                    min={0.1}
                    max={10}
                    step={0.1}
                    {...register(`rules.${index}.weight`, { valueAsNumber: true })}
                  />
                </Field>
              </div>
              <Field
                label="Configuration (JSON)"
                hint={CONFIG_HINT[rules[index]?.ruleType ?? DataContractRuleType.Volume] ?? '{}'}
                className="mt-2"
              >
                <Textarea rows={2} {...register(`rules.${index}.configuration`)} />
              </Field>
            </Card>
          ))}
        </div>
      </form>
      )}
    </Dialog>
  );
}
