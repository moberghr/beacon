import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ArrowLeftRight, LayoutGrid, Plus, X } from 'lucide-react';
import {
  PageHeader,
  Button,
  Card,
  Field,
  Input,
  Select,
  Textarea,
} from '@/components/beacon';
import { EmptyState } from '@/components/data/EmptyState';
import type { DashboardWidgetData } from '@/api/generated/beacon-api';
import { useDashboardQuery, useAddWidget, useDeleteWidget, WIDGET_TYPE, WIDGET_TYPE_LABEL } from './queries';

const WIDGET_TYPE_OPTIONS = [
  { value: WIDGET_TYPE.KpiCard, label: WIDGET_TYPE_LABEL[WIDGET_TYPE.KpiCard] },
  { value: WIDGET_TYPE.Chart, label: WIDGET_TYPE_LABEL[WIDGET_TYPE.Chart] },
  { value: WIDGET_TYPE.Table, label: WIDGET_TYPE_LABEL[WIDGET_TYPE.Table] },
  { value: WIDGET_TYPE.Gauge, label: WIDGET_TYPE_LABEL[WIDGET_TYPE.Gauge] },
  { value: WIDGET_TYPE.Mermaid, label: WIDGET_TYPE_LABEL[WIDGET_TYPE.Mermaid] },
] as const;

const DEFAULT_CONFIGS: Record<number, string> = {
  [WIDGET_TYPE.KpiCard]: JSON.stringify({ value: 0, unit: '' }, null, 2),
  [WIDGET_TYPE.Chart]: JSON.stringify({ chartType: 'bar', data: [] }, null, 2),
  [WIDGET_TYPE.Table]: JSON.stringify({ columns: [], rows: [] }, null, 2),
  [WIDGET_TYPE.Gauge]: JSON.stringify({ value: 0, max: 100, label: '' }, null, 2),
  [WIDGET_TYPE.Mermaid]: JSON.stringify({ code: 'graph LR\n  A --> B' }, null, 2),
};

interface AddWidgetFormState {
  title: string;
  widgetType: number;
  configurationJson: string;
  positionX: number;
  positionY: number;
  width: number;
  height: number;
}

const INITIAL_FORM: AddWidgetFormState = {
  title: '',
  widgetType: WIDGET_TYPE.KpiCard,
  configurationJson: DEFAULT_CONFIGS[WIDGET_TYPE.KpiCard],
  positionX: 0,
  positionY: 0,
  width: 6,
  height: 2,
};

export default function DashboardBuilderPage() {
  const params = useParams<{ id: string }>();
  const id = params.id ? Number(params.id) : undefined;
  const { data, isLoading } = useDashboardQuery(id);
  const addWidget = useAddWidget(id ?? 0);
  const deleteWidget = useDeleteWidget(id ?? 0);

  const [showAddForm, setShowAddForm] = useState(false);
  const [form, setForm] = useState<AddWidgetFormState>(INITIAL_FORM);
  const [configError, setConfigError] = useState<string | null>(null);

  const widgets = data?.widgets ?? [];

  const handleTypeChange = (widgetType: number) => {
    setForm(prev => ({
      ...prev,
      widgetType,
      configurationJson: DEFAULT_CONFIGS[widgetType] ?? '{}',
    }));
  };

  const validateConfig = (json: string): boolean => {
    try {
      JSON.parse(json);
      setConfigError(null);
      return true;
    } catch {
      setConfigError('Invalid JSON');
      return false;
    }
  };

  const handleAddWidget = async () => {
    if (!form.title.trim()) return;
    if (!validateConfig(form.configurationJson)) return;

    await addWidget.mutateAsync({
      title: form.title.trim(),
      widgetType: form.widgetType,
      configurationJson: form.configurationJson,
      positionX: form.positionX,
      positionY: form.positionY,
      width: form.width,
      height: form.height,
      refreshIntervalSeconds: null,
    });
    setShowAddForm(false);
    setForm(INITIAL_FORM);
  };

  const handleDeleteWidget = (w: DashboardWidgetData) => {
    if (w.id == null) return;
    if (!confirm(`Remove widget "${w.title || 'Untitled'}"?`)) return;
    deleteWidget.mutate(w.id);
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="nodes"
        eyebrow="Dashboard builder"
        prefix="Editing"
        emphasis={data?.name ?? 'dashboard'}
        sub={<span className="text-text-muted">{isLoading ? 'Loading…' : `${widgets.length} widget${widgets.length === 1 ? '' : 's'}`}</span>}
        actions={
          <div className="flex gap-2">
            <Button
              variant="primary"
              type="button"
              onClick={() => setShowAddForm(v => !v)}
              icon={<Plus />}
            >
              Add widget
            </Button>
            <Link to={id !== undefined ? `/dashboards/${id}` : '/dashboards'}>
              <Button icon={<ArrowLeftRight />}>View dashboard</Button>
            </Link>
          </div>
        }
      />

      {showAddForm && (
        <Card className="p-4">
          <div className="font-semibold mb-3">New widget</div>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Title">
              <Input
                type="text"
                value={form.title}
                onChange={e => setForm(prev => ({ ...prev, title: e.target.value }))}
                placeholder="Widget title"
              />
            </Field>
            <Field label="Widget type">
              <Select
                value={form.widgetType}
                onChange={e => handleTypeChange(Number(e.target.value))}
              >
                {WIDGET_TYPE_OPTIONS.map(o => (
                  <option key={o.value} value={o.value}>{o.label}</option>
                ))}
              </Select>
            </Field>
            <Field label="Position X">
              <Input
                type="number"
                value={form.positionX}
                min={0}
                onChange={e => setForm(prev => ({ ...prev, positionX: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Position Y">
              <Input
                type="number"
                value={form.positionY}
                min={0}
                onChange={e => setForm(prev => ({ ...prev, positionY: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Width">
              <Input
                type="number"
                value={form.width}
                min={1}
                max={12}
                onChange={e => setForm(prev => ({ ...prev, width: Number(e.target.value) }))}
              />
            </Field>
            <Field label="Height">
              <Input
                type="number"
                value={form.height}
                min={1}
                max={12}
                onChange={e => setForm(prev => ({ ...prev, height: Number(e.target.value) }))}
              />
            </Field>
          </div>
          <Field label="Configuration JSON" className="mt-3">
            <Textarea
              rows={8}
              className="mono"
              aria-invalid={!!configError}
              value={form.configurationJson}
              onChange={e => {
                setForm(prev => ({ ...prev, configurationJson: e.target.value }));
                validateConfig(e.target.value);
              }}
            />
            {configError && <span className="text-xs text-crit">{configError}</span>}
          </Field>
          <div className="flex gap-2 mt-3">
            <Button
              variant="primary"
              type="button"
              onClick={handleAddWidget}
              disabled={addWidget.isPending || !form.title.trim()}
            >
              {addWidget.isPending ? 'Adding…' : 'Add widget'}
            </Button>
            <Button
              type="button"
              onClick={() => { setShowAddForm(false); setForm(INITIAL_FORM); }}
            >
              Cancel
            </Button>
          </div>
        </Card>
      )}

      {!isLoading && widgets.length === 0 && !showAddForm && (
        <EmptyState
          icon={<LayoutGrid />}
          title="No widgets yet"
          description='Click "Add widget" to create the first widget on this dashboard.'
        />
      )}

      {widgets.length > 0 && (
        <Card className="overflow-hidden">
          {widgets.map((w, idx) => (
            <div
              key={w.id ?? idx}
              className="grid grid-cols-[1fr_auto_auto_auto_auto_200px_auto] items-center gap-3 px-4 py-3 border-b border-border last:border-b-0"
            >
              <div>
                <div className="font-semibold text-text">{w.title || 'Untitled'}</div>
                <div className="text-text-muted text-xs mt-0.5">
                  {WIDGET_TYPE_LABEL[w.widgetType ?? 0] ?? `type ${w.widgetType}`}
                </div>
              </div>
              <div className="text-text-muted text-xs">X: {w.positionX ?? 0}</div>
              <div className="text-text-muted text-xs">Y: {w.positionY ?? 0}</div>
              <div className="text-text-muted text-xs">{w.width ?? 0}w</div>
              <div className="text-text-muted text-xs">{w.height ?? 0}h</div>
              <div className="text-text-muted mono text-[11px] truncate">
                {w.configurationJson ? w.configurationJson.slice(0, 60) + (w.configurationJson.length > 60 ? '…' : '') : '—'}
              </div>
              <Button
                type="button"
                onClick={() => handleDeleteWidget(w)}
                disabled={deleteWidget.isPending}
                title="Remove widget"
                icon={<X />}
              />
            </div>
          ))}
        </Card>
      )}
    </div>
  );
}
