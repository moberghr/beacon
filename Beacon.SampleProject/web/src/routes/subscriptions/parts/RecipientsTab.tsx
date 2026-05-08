import { useState } from 'react';
import { Icon } from '@/components/Icon';
import { EmptyState } from '@/components/data/EmptyState';
import { Dialog } from '@/components/ui/Dialog';
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

  const entries = recipientsQuery.data?.entries ?? [];
  const candidates = entries.filter(e => !existingIds.includes(e.id));

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
          <button
            type="button"
            className="btn btn--primary"
            onClick={onSubmit}
            disabled={selected.size === 0 || add.isPending}
          >
            {add.isPending ? 'Adding…' : `Add${selected.size > 0 ? ` (${selected.size})` : ''}`}
          </button>
        </>
      }
    >
      {recipientsQuery.isLoading ? (
        <div className="muted">Loading recipients…</div>
      ) : candidates.length === 0 ? (
        <EmptyState
          icon={<Icon.Users size={20} />}
          title="No more recipients"
          description="All existing recipients are already attached to this subscription."
        />
      ) : (
        <ul style={{ listStyle: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 8 }}>
          {candidates.map(r => (
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
        </ul>
      )}
    </Dialog>
  );
}
