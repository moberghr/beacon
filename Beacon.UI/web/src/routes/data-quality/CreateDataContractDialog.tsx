import { useEffect, useMemo, useState } from 'react';
import { toast } from 'sonner';
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
import {
  DataContractRuleType,
  DataContractSeverity,
  describeContractError,
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

interface RuleEditModel {
  id?: number;
  name: string;
  description: string;
  ruleType: number;
  columnName: string;
  configuration: string;
  severity: number;
  weight: number;
  isEnabled: boolean;
}

function blankRule(index: number): RuleEditModel {
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

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [dataSourceId, setDataSourceId] = useState<number | null>(null);
  const [schemaName, setSchemaName] = useState('');
  const [tableName, setTableName] = useState('');
  const [cronExpression, setCronExpression] = useState('0 */6 * * *');
  const [isEnabled, setIsEnabled] = useState(true);
  const [alertOnFailure, setAlertOnFailure] = useState(true);
  const [failureThreshold, setFailureThreshold] = useState(80);
  const [rules, setRules] = useState<RuleEditModel[]>([]);
  const [recipientIds, setRecipientIds] = useState<number[]>([]);

  // Pre-fill from existing contract when editing.
  useEffect(() => {
    const c = existingQ.data;
    if (!c) return;
    setName(c.name);
    setDescription(c.description ?? '');
    setDataSourceId(c.dataSourceId);
    setSchemaName(c.schemaName);
    setTableName(c.tableName);
    setCronExpression(c.cronExpression);
    setIsEnabled(c.isEnabled);
    setAlertOnFailure(c.alertOnFailure);
    setFailureThreshold(c.failureThresholdScore);
    setRecipientIds(c.recipients.map(r => r.id));
    setRules(
      c.rules.map(r => ({
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
    );
  }, [existingQ.data]);

  const isValid = useMemo(
    () =>
      name.trim().length > 0 &&
      schemaName.trim().length > 0 &&
      tableName.trim().length > 0 &&
      cronExpression.trim().length > 0 &&
      dataSourceId !== null &&
      rules.every(r => r.name.trim().length > 0),
    [name, schemaName, tableName, cronExpression, dataSourceId, rules],
  );

  const submitting = createMutation.isPending || updateMutation.isPending;

  function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (!isValid || dataSourceId === null) return;

    const rulesData: DataContractRuleData[] = rules.map(r => ({
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
      dataSourceId,
      schemaName,
      tableName,
      name,
      description: description || null,
      cronExpression,
      isEnabled,
      ownerUserId: null,
      alertOnFailure,
      failureThresholdScore: failureThreshold,
      rules: rulesData,
      recipientIds: alertOnFailure ? recipientIds : null,
    };

    if (isEdit) {
      updateMutation.mutate(payload, {
        onSuccess: () => onClose(true),
        onError: err => toast.error(describeContractError(err, 'Update failed')),
      });
    } else {
      createMutation.mutate(payload, {
        onSuccess: () => onClose(true),
        onError: err => toast.error(describeContractError(err, 'Create failed')),
      });
    }
  }

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
            disabled={!isValid || submitting}
          >
            {submitting ? 'Saving…' : isEdit ? 'Update' : 'Create'}
          </Button>
        </>
      }
    >
      <form id="data-contract-form" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-3">
          <Field label={<>Contract name {REQ}</>}>
            <Input
              value={name}
              onChange={e => setName(e.target.value)}
              required
              maxLength={200}
            />
          </Field>

          <Field label="Description">
            <Textarea
              rows={2}
              value={description}
              onChange={e => setDescription(e.target.value)}
              maxLength={1000}
            />
          </Field>

          <Field label={<>Data source {REQ}</>}>
            <Select
              value={dataSourceId ?? ''}
              onChange={e => setDataSourceId(e.target.value ? Number(e.target.value) : null)}
              required
            >
              <option value="">Select…</option>
              {(dataSources.data?.entries ?? []).map(d => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </Select>
          </Field>

          <div className="grid grid-cols-2 gap-3">
            <Field label={<>Schema name {REQ}</>}>
              <Input
                value={schemaName}
                onChange={e => setSchemaName(e.target.value)}
                required
                maxLength={200}
              />
            </Field>
            <Field label={<>Table name {REQ}</>}>
              <Input
                value={tableName}
                onChange={e => setTableName(e.target.value)}
                required
                maxLength={200}
              />
            </Field>
          </div>

          <div className="grid grid-cols-[2fr_1fr] gap-3">
            <Field label={<>Cron expression {REQ}</>} hint="e.g. 0 */6 * * * (every 6 hours)">
              <Input
                value={cronExpression}
                onChange={e => setCronExpression(e.target.value)}
                required
                maxLength={100}
              />
            </Field>
            <Field label="Failure threshold (%)">
              <Input
                type="number"
                min={0}
                max={100}
                value={failureThreshold}
                onChange={e => setFailureThreshold(Number(e.target.value))}
              />
            </Field>
          </div>

          <div className="flex gap-4 text-sm">
            <label className="flex items-center gap-1.5">
              <input
                type="checkbox"
                checked={isEnabled}
                onChange={e => setIsEnabled(e.target.checked)}
              />
              Enabled
            </label>
            <label className="flex items-center gap-1.5">
              <input
                type="checkbox"
                checked={alertOnFailure}
                onChange={e => setAlertOnFailure(e.target.checked)}
              />
              Alert on failure
            </label>
          </div>

          {alertOnFailure && (
            <Field label="Notification recipients">
              <Select
                multiple
                size={4}
                value={recipientIds.map(String)}
                onChange={e => {
                  const opts = Array.from(e.target.selectedOptions).map(o => Number(o.value));
                  setRecipientIds(opts);
                }}
              >
                {(recipientsQ.data?.entries ?? []).map(r => (
                  <option key={r.id} value={r.id}>{r.name}</option>
                ))}
              </Select>
            </Field>
          )}

          <hr className="border-0 border-t border-border" />

          <div className="flex justify-between items-center">
            <strong>Rules ({rules.length})</strong>
            <Button
              type="button"
              size="sm"
              onClick={() => setRules(prev => [...prev, blankRule(prev.length)])}
            >
              Add rule
            </Button>
          </div>

          {rules.map((rule, index) => (
            <Card key={index} className="p-3">
              <div className="flex justify-between mb-2">
                <strong>Rule {index + 1}</strong>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => setRules(prev => prev.filter((_, i) => i !== index))}
                >
                  Remove
                </Button>
              </div>
              <div className="grid grid-cols-2 gap-2">
                <Field label={<>Name {REQ}</>}>
                  <Input
                    value={rule.name}
                    onChange={e =>
                      setRules(prev => prev.map((r, i) => (i === index ? { ...r, name: e.target.value } : r)))
                    }
                    required
                  />
                </Field>
                <Field label="Type">
                  <Select
                    value={rule.ruleType}
                    onChange={e =>
                      setRules(prev =>
                        prev.map((r, i) => (i === index ? { ...r, ruleType: Number(e.target.value) } : r)),
                      )
                    }
                  >
                    {Object.entries(RULE_TYPE_LABEL).map(([k, label]) => (
                      <option key={k} value={k}>{label}</option>
                    ))}
                  </Select>
                </Field>
              </div>
              <div className="grid grid-cols-3 gap-2 mt-2">
                <Field label="Column (optional)">
                  <Input
                    value={rule.columnName}
                    onChange={e =>
                      setRules(prev =>
                        prev.map((r, i) => (i === index ? { ...r, columnName: e.target.value } : r)),
                      )
                    }
                  />
                </Field>
                <Field label="Severity">
                  <Select
                    value={rule.severity}
                    onChange={e =>
                      setRules(prev =>
                        prev.map((r, i) => (i === index ? { ...r, severity: Number(e.target.value) } : r)),
                      )
                    }
                  >
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
                    value={rule.weight}
                    onChange={e =>
                      setRules(prev =>
                        prev.map((r, i) => (i === index ? { ...r, weight: Number(e.target.value) } : r)),
                      )
                    }
                  />
                </Field>
              </div>
              <Field label="Configuration (JSON)" hint={CONFIG_HINT[rule.ruleType] ?? '{}'} className="mt-2">
                <Textarea
                  rows={2}
                  value={rule.configuration}
                  onChange={e =>
                    setRules(prev =>
                      prev.map((r, i) => (i === index ? { ...r, configuration: e.target.value } : r)),
                    )
                  }
                />
              </Field>
            </Card>
          ))}
        </div>
      </form>
    </Dialog>
  );
}
