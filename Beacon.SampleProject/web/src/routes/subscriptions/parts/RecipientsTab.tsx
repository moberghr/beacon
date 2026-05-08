import { useMemo, useState } from 'react';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { Dialog } from '@/components/ui/Dialog';
import { Stepper } from '@/components/ui/Stepper';
import {
  NOTIFICATION_TYPE_LABEL,
  useRecipientsQuery,
} from '@/routes/recipients/queries';
import {
  useAddSubscriptionRecipients,
  useRemoveSubscriptionRecipient,
  type SubscriptionDetail,
} from '../queries';

interface RecipientsTabProps {
  subscription: SubscriptionDetail;
  canWrite: boolean;
  isAdmin: boolean;
}

export function RecipientsTab({ subscription, canWrite, isAdmin }: RecipientsTabProps) {
  const [picking, setPicking] = useState(false);

  const remove = useRemoveSubscriptionRecipient(subscription.id);
  const recipients = subscription.recipients;

  const onRemove = (recipientId: number) => {
    if (!isAdmin || recipients.length <= 1) return;
    remove.mutate(recipientId);
  };

  return (
    <div style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
        }}
      >
        <div style={{ fontWeight: 600 }}>
          Notification recipients
          <span className="muted" style={{ marginLeft: 8 }}>
            ({recipients.length})
          </span>
        </div>
        <button
          type="button"
          className="btn btn--primary"
          onClick={() => setPicking(true)}
          disabled={!canWrite}
        >
          <Icon.Plus size={14} className="btn__icon" /> Add recipient
        </button>
      </div>

      {recipients.length === 0 ? (
        <EmptyState
          icon={<Icon.Users size={20} />}
          title="No recipients yet"
          description="Add at least one recipient so notifications can be delivered."
        />
      ) : (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(240px, 1fr))',
            gap: 12,
          }}
        >
          {recipients.map(r => (
            <div
              key={r.id}
              className="card"
              style={{ padding: 12, display: 'flex', flexDirection: 'column', gap: 6 }}
            >
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                <span className="pill pill--info mono" style={{ fontSize: 10 }}>
                  {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                </span>
                <button
                  type="button"
                  className="icon-btn"
                  onClick={() => onRemove(r.id)}
                  disabled={!isAdmin || recipients.length <= 1 || remove.isPending}
                  aria-label={`Remove ${r.name}`}
                  title={
                    recipients.length <= 1
                      ? 'A subscription must keep at least one recipient.'
                      : 'Remove recipient'
                  }
                >
                  <Icon.Alert size={12} />
                </button>
              </div>
              <div style={{ fontWeight: 600 }}>{r.name}</div>
              <div className="mono muted" style={{ fontSize: 12, wordBreak: 'break-all' }}>
                {r.destination}
              </div>
              {r.description && (
                <div className="muted" style={{ fontSize: 12 }}>{r.description}</div>
              )}
            </div>
          ))}
        </div>
      )}

      {picking && (
        <RecipientPicker
          subscriptionId={subscription.id}
          existingIds={recipients.map(r => r.id)}
          onClose={() => setPicking(false)}
        />
      )}
    </div>
  );
}

interface RecipientPickerProps {
  subscriptionId: number;
  existingIds: number[];
  onClose: () => void;
}

function RecipientPicker({ existingIds, onClose, subscriptionId }: RecipientPickerProps) {
  const recipientsQuery = useRecipientsQuery();
  const add = useAddSubscriptionRecipients(subscriptionId);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [step, setStep] = useState<0 | 1>(0);
  const [search, setSearch] = useState('');

  const entries = recipientsQuery.data?.entries ?? [];
  const candidates = useMemo(
    () => entries.filter(e => !existingIds.includes(e.id)),
    [entries, existingIds]
  );
  const visible = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return candidates;
    return candidates.filter(c =>
      c.name.toLowerCase().includes(q) || c.destination.toLowerCase().includes(q)
    );
  }, [candidates, search]);
  const selectedRecipients = useMemo(
    () => candidates.filter(c => selected.has(c.id)),
    [candidates, selected]
  );

  const toggle = (id: number) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const onSubmit = () => {
    if (selected.size === 0) return;
    add.mutate([...selected], {
      onSuccess: () => onClose(),
    });
  };

  const goNext = () => {
    if (step === 0 && selected.size > 0) setStep(1);
    else if (step === 1) onSubmit();
  };

  return (
    <Dialog
      open
      onClose={onClose}
      title="Add recipients"
      size="md"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={add.isPending}>
            Cancel
          </button>
          {step === 1 && (
            <button type="button" className="btn" onClick={() => setStep(0)} disabled={add.isPending}>
              Back
            </button>
          )}
          <button
            type="button"
            className="btn btn--primary"
            onClick={goNext}
            disabled={selected.size === 0 || add.isPending}
          >
            {step === 0
              ? `Next${selected.size > 0 ? ` (${selected.size})` : ''}`
              : add.isPending
                ? 'Adding…'
                : `Add ${selected.size}`}
          </button>
        </>
      }
    >
      <Stepper
        steps={[
          { id: 'select', title: 'Select', description: 'Pick recipients to attach' },
          { id: 'review', title: 'Review', description: 'Confirm assignment' },
        ]}
        current={step}
        onStepClick={i => i === 0 && setStep(0)}
      />

      {step === 0 && (
        <div style={{ marginTop: 12 }}>
          {recipientsQuery.isLoading ? (
            <div className="muted">Loading recipients…</div>
          ) : candidates.length === 0 ? (
            <EmptyState
              icon={<Icon.Users size={20} />}
              title="No more recipients"
              description="All existing recipients are already attached to this subscription."
            />
          ) : (
            <>
              <input
                type="search"
                value={search}
                onChange={e => setSearch(e.target.value)}
                placeholder="Search by name or destination…"
                className="q-input"
                style={{ width: '100%', marginBottom: 8 }}
              />
              <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8, maxHeight: 320, overflowY: 'auto' }}>
                {visible.map(r => (
                  <li key={r.id}>
                    <label
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 8,
                        padding: '6px 8px',
                        border: '1px solid var(--border)',
                        borderRadius: 8,
                        cursor: 'pointer',
                      }}
                    >
                      <input
                        type="checkbox"
                        checked={selected.has(r.id)}
                        onChange={() => toggle(r.id)}
                      />
                      <span style={{ fontWeight: 500 }}>{r.name}</span>
                      <span className="muted mono" style={{ fontSize: 12 }}>
                        {r.destination}
                      </span>
                      <span
                        className="pill pill--neutral mono"
                        style={{ fontSize: 10, marginLeft: 'auto' }}
                      >
                        {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                      </span>
                    </label>
                  </li>
                ))}
                {visible.length === 0 && (
                  <li className="muted" style={{ padding: 12, textAlign: 'center' }}>
                    No matches for "{search}".
                  </li>
                )}
              </ul>
            </>
          )}
        </div>
      )}

      {step === 1 && (
        <div style={{ marginTop: 12 }}>
          <div className="muted" style={{ marginBottom: 8 }}>
            About to attach <strong>{selectedRecipients.length}</strong> recipient{selectedRecipients.length === 1 ? '' : 's'} to this subscription.
          </div>
          <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 6 }}>
            {selectedRecipients.map(r => (
              <li
                key={r.id}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  padding: '6px 8px',
                  border: '1px solid var(--border)',
                  borderRadius: 8,
                }}
              >
                <Icon.Check size={12} className="muted" />
                <span style={{ fontWeight: 500 }}>{r.name}</span>
                <span className="muted mono" style={{ fontSize: 12 }}>
                  {r.destination}
                </span>
                <span className="pill pill--neutral mono" style={{ fontSize: 10, marginLeft: 'auto' }}>
                  {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Dialog>
  );
}
