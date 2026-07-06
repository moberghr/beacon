import { useMemo, useState } from 'react';
import { Check, Plus, Users, X } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { Dialog } from '@/components/ui/Dialog';
import { Stepper } from '@/components/ui/Stepper';
import { Button, Card, Input, Pill } from '@/components/beacon';
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
    <div className="p-4 flex flex-col gap-3">
      <div className="flex items-center justify-between">
        <div className="font-semibold">
          Notification recipients
          <span className="text-text-muted ml-2">({recipients.length})</span>
        </div>
        <Button
          variant="primary"
          icon={<Plus />}
          onClick={() => setPicking(true)}
          disabled={!canWrite}
        >
          Add recipient
        </Button>
      </div>

      {recipients.length === 0 ? (
        <EmptyState
          icon={<Users />}
          title="No recipients yet"
          description="Add at least one recipient so notifications can be delivered."
        />
      ) : (
        <div className="grid gap-3 grid-cols-[repeat(auto-fill,minmax(240px,1fr))]">
          {recipients.map(r => (
            <Card key={r.id} className="p-3 flex flex-col gap-1.5">
              <div className="flex items-center justify-between">
                <Pill tone="info">
                  {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                </Pill>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => onRemove(r.id)}
                  disabled={!isAdmin || recipients.length <= 1 || remove.isPending}
                  aria-label={`Remove ${r.name}`}
                  title={
                    recipients.length <= 1
                      ? 'A subscription must keep at least one recipient.'
                      : 'Remove recipient'
                  }
                  icon={<X />}
                />
              </div>
              <div className="font-semibold">{r.name}</div>
              <div className="mono text-text-muted text-xs break-all">{r.destination}</div>
              {r.description && (
                <div className="text-text-muted text-xs">{r.description}</div>
              )}
            </Card>
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
          <Button onClick={onClose} disabled={add.isPending}>Cancel</Button>
          {step === 1 && (
            <Button onClick={() => setStep(0)} disabled={add.isPending}>Back</Button>
          )}
          <Button
            variant="primary"
            onClick={goNext}
            disabled={selected.size === 0 || add.isPending}
          >
            {step === 0
              ? `Next${selected.size > 0 ? ` (${selected.size})` : ''}`
              : add.isPending
                ? 'Adding…'
                : `Add ${selected.size}`}
          </Button>
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
        <div className="mt-3">
          {recipientsQuery.isLoading ? (
            <div className="text-text-muted">Loading recipients…</div>
          ) : candidates.length === 0 ? (
            <EmptyState
              icon={<Users />}
              title="No more recipients"
              description="All existing recipients are already attached to this subscription."
            />
          ) : (
            <>
              <Input
                type="search"
                value={search}
                onChange={e => setSearch(e.target.value)}
                placeholder="Search by name or destination…"
                className="mb-2"
              />
              <ul className="list-none p-0 m-0 flex flex-col gap-2 max-h-80 overflow-y-auto">
                {visible.map(r => (
                  <li key={r.id}>
                    <label className="flex items-center gap-2 px-2 py-1.5 border border-border rounded-sm cursor-pointer">
                      <input
                        type="checkbox"
                        checked={selected.has(r.id)}
                        onChange={() => toggle(r.id)}
                      />
                      <span className="font-medium">{r.name}</span>
                      <span className="text-text-muted mono text-xs">{r.destination}</span>
                      <Pill className="ml-auto">
                        {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                      </Pill>
                    </label>
                  </li>
                ))}
                {visible.length === 0 && (
                  <li className="text-text-muted p-3 text-center">
                    No matches for "{search}".
                  </li>
                )}
              </ul>
            </>
          )}
        </div>
      )}

      {step === 1 && (
        <div className="mt-3">
          <div className="text-text-muted mb-2">
            About to attach <strong>{selectedRecipients.length}</strong> recipient{selectedRecipients.length === 1 ? '' : 's'} to this subscription.
          </div>
          <ul className="list-none p-0 m-0 flex flex-col gap-1.5">
            {selectedRecipients.map(r => (
              <li
                key={r.id}
                className="flex items-center gap-2 px-2 py-1.5 border border-border rounded-sm"
              >
                <Check className="size-3 text-text-muted" />
                <span className="font-medium">{r.name}</span>
                <span className="text-text-muted mono text-xs">{r.destination}</span>
                <Pill className="ml-auto">
                  {NOTIFICATION_TYPE_LABEL[r.notificationType] ?? r.notificationType}
                </Pill>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Dialog>
  );
}
