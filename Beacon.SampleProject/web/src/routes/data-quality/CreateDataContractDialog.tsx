import { useEffect, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
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
          <button type="button" className="btn" onClick={() => onClose(false)} disabled={submitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="data-contract-form"
            className="btn btn--primary"
            disabled={!isValid || submitting}
          >
            {submitting ? 'Saving…' : isEdit ? 'Update' : 'Create'}
          </button>
        </>
      }
    >
      <form id="data-contract-form" onSubmit={handleSubmit}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <label className="field">
            <span className="field__label">Contract name</span>
            <input
              className="input"
              value={name}
              onChange={e => setName(e.target.value)}
              required
              maxLength={200}
            />
          </label>

          <label className="field">
            <span className="field__label">Description</span>
            <textarea
              className="textarea"
              rows={2}
              value={description}
              onChange={e => setDescription(e.target.value)}
              maxLength={1000}
            />
          </label>

          <label className="field">
            <span className="field__label">Data source</span>
            <select
              className="select"
              value={dataSourceId ?? ''}
              onChange={e => setDataSourceId(e.target.value ? Number(e.target.value) : null)}
              required
            >
              <option value="">Select…</option>
              {(dataSources.data?.entries ?? []).map(d => (
                <option key={d.id} value={d.id}>{d.name}</option>
              ))}
            </select>
          </label>

          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <label className="field">
              <span className="field__label">Schema name</span>
              <input
                className="input"
                value={schemaName}
                onChange={e => setSchemaName(e.target.value)}
                required
                maxLength={200}
              />
            </label>
            <label className="field">
              <span className="field__label">Table name</span>
              <input
                className="input"
                value={tableName}
                onChange={e => setTableName(e.target.value)}
                required
                maxLength={200}
              />
            </label>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: 12 }}>
            <label className="field">
              <span className="field__label">Cron expression</span>
              <input
                className="input"
                value={cronExpression}
                onChange={e => setCronExpression(e.target.value)}
                required
                maxLength={100}
              />
              <span className="field__hint">e.g. 0 */6 * * * (every 6 hours)</span>
            </label>
            <label className="field">
              <span className="field__label">Failure threshold (%)</span>
              <input
                className="input"
                type="number"
                min={0}
                max={100}
                value={failureThreshold}
                onChange={e => setFailureThreshold(Number(e.target.value))}
              />
            </label>
          </div>

          <div style={{ display: 'flex', gap: 16 }}>
            <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <input
                type="checkbox"
                checked={isEnabled}
                onChange={e => setIsEnabled(e.target.checked)}
              />
              Enabled
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <input
                type="checkbox"
                checked={alertOnFailure}
                onChange={e => setAlertOnFailure(e.target.checked)}
              />
              Alert on failure
            </label>
          </div>

          {alertOnFailure && (
            <label className="field">
              <span className="field__label">Notification recipients</span>
              <select
                className="select"
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
              </select>
            </label>
          )}

          <hr style={{ border: 0, borderTop: '1px solid var(--border)' }} />

          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
            <strong>Rules ({rules.length})</strong>
            <button
              type="button"
              className="btn btn--sm"
              onClick={() => setRules(prev => [...prev, blankRule(prev.length)])}
            >
              Add rule
            </button>
          </div>

          {rules.map((rule, index) => (
            <div key={index} className="card" style={{ padding: 12 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 8 }}>
                <strong>Rule {index + 1}</strong>
                <button
                  type="button"
                  className="btn btn--ghost btn--sm"
                  onClick={() => setRules(prev => prev.filter((_, i) => i !== index))}
                >
                  Remove
                </button>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                <label className="field">
                  <span className="field__label">Name</span>
                  <input
                    className="input"
                    value={rule.name}
                    onChange={e =>
                      setRules(prev => prev.map((r, i) => (i === index ? { ...r, name: e.target.value } : r)))
                    }
                    required
                  />
                </label>
                <label className="field">
                  <span className="field__label">Type</span>
                  <select
                    className="select"
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
                  </select>
                </label>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 8, marginTop: 8 }}>
                <label className="field">
                  <span className="field__label">Column (optional)</span>
                  <input
                    className="input"
                    value={rule.columnName}
                    onChange={e =>
                      setRules(prev =>
                        prev.map((r, i) => (i === index ? { ...r, columnName: e.target.value } : r)),
                      )
                    }
                  />
                </label>
                <label className="field">
                  <span className="field__label">Severity</span>
                  <select
                    className="select"
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
                  </select>
                </label>
                <label className="field">
                  <span className="field__label">Weight</span>
                  <input
                    className="input"
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
                </label>
              </div>
              <label className="field" style={{ marginTop: 8 }}>
                <span className="field__label">Configuration (JSON)</span>
                <textarea
                  className="textarea"
                  rows={2}
                  value={rule.configuration}
                  onChange={e =>
                    setRules(prev =>
                      prev.map((r, i) => (i === index ? { ...r, configuration: e.target.value } : r)),
                    )
                  }
                />
                <span className="field__hint">{CONFIG_HINT[rule.ruleType] ?? '{}'}</span>
              </label>
            </div>
          ))}
        </div>
      </form>
    </Dialog>
  );
}
