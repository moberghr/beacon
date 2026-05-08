import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Dialog } from '@/components/ui/Dialog';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useCreateQuery, useQueriesListQuery, type QueryListItem } from './queries';

const COLUMNS: Column<QueryListItem>[] = [
  {
    key: 'name',
    header: 'Name',
    render: q => (
      <div>
        <div style={{ fontWeight: 600, color: 'var(--text)' }}>{q.name}</div>
        {q.description && (
          <div className="muted" style={{ fontSize: 12, marginTop: 2 }}>{q.description}</div>
        )}
      </div>
    ),
  },
  {
    key: 'source',
    header: 'Source',
    render: q => q.aiActorName ? (
      <span className="pill pill--info mono" style={{ fontSize: 10 }}>{q.aiActorName}</span>
    ) : (
      <span className="muted">user-defined</span>
    ),
  },
  {
    key: 'steps',
    header: 'Steps',
    render: q => formatNumber(q.steps.length),
  },
  {
    key: 'subs',
    header: 'Subscriptions',
    render: q => formatNumber(q.subscriptionsCount),
  },
  {
    key: 'created',
    header: 'Created',
    render: q => <span className="mono">{formatDateTime(q.createdTime)}</span>,
  },
];

export default function QueriesListPage() {
  const [search, setSearch] = useState('');
  const [creating, setCreating] = useState(false);
  const navigate = useNavigate();
  const { data, isLoading } = useQueriesListQuery({ searchTerm: search.trim() || undefined });
  const entries = data?.items ?? [];

  return (
    <div className="page">
      <PageHeader
        title="Queries"
        sub={
          <span className="muted">
            {data ? `${formatNumber(data.totalCount)} total` : isLoading ? 'Loading…' : ''}
          </span>
        }
        actions={
          <button type="button" className="btn btn--primary" onClick={() => setCreating(true)}>
            <Icon.Plus size={14} className="btn__icon" /> New query
          </button>
        }
      />

      <div style={{ marginBottom: 12, display: 'flex', gap: 8 }}>
        <input
          type="search"
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="Search queries by name…"
          className="q-input"
          style={{ maxWidth: 360 }}
        />
      </div>

      <div className="card">
        <DataTable
          columns={COLUMNS}
          rows={entries}
          rowKey={q => q.queryId}
          gridTemplate="2fr 1fr 0.6fr 0.8fr 1.2fr"
          onRowClick={q => navigate(`/queries/${q.queryId}`)}
          empty={
            <EmptyState
              icon={<Icon.Layers size={20} />}
              title={isLoading ? 'Loading queries…' : 'No queries yet'}
              description="Click + New query to start; you'll fill in the SQL on the editor page."
            />
          }
        />
      </div>

      <CreateQueryDialog
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={id => {
          setCreating(false);
          toast.success('Query created');
          navigate(`/queries/${id}/edit`);
        }}
      />
    </div>
  );
}

interface CreateQueryDialogProps {
  open: boolean;
  onClose: () => void;
  onCreated: (queryId: number) => void;
}

function CreateQueryDialog({ open, onClose, onCreated }: CreateQueryDialogProps) {
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const create = useCreateQuery();

  const trimmedName = name.trim();
  const submit = () => {
    if (!trimmedName) return;
    create.mutate(
      { name: trimmedName, description: description.trim() || null },
      { onSuccess: result => onCreated(result.queryId) },
    );
  };

  if (!open) return null;

  return (
    <Dialog
      open
      onClose={onClose}
      title="New query"
      size="md"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={create.isPending}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn--primary"
            onClick={submit}
            disabled={!trimmedName || create.isPending}
          >
            {create.isPending ? 'Creating…' : 'Create & edit'}
          </button>
        </>
      }
    >
      <div className="q-field">
        <label className="q-label" htmlFor="new-query-name">Name<span className="q-label__req">*</span></label>
        <input
          id="new-query-name"
          type="text"
          className="q-input"
          value={name}
          autoFocus
          onChange={e => setName(e.target.value)}
          maxLength={200}
        />
      </div>
      <div className="q-field" style={{ marginTop: 12 }}>
        <label className="q-label" htmlFor="new-query-desc">Description</label>
        <textarea
          id="new-query-desc"
          className="q-input"
          value={description}
          onChange={e => setDescription(e.target.value)}
          rows={3}
          maxLength={1000}
        />
        <div className="q-help">You can add SQL steps and parameters on the editor page after creation.</div>
      </div>
    </Dialog>
  );
}
