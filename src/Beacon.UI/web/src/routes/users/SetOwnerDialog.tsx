import { useState } from 'react';
import { Button, Field, Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/beacon';
import { SearchPicker } from '@/components/data/SearchPicker';

interface OwnerCandidate {
  id: number;
  userName: string;
  displayName?: string | null;
  email?: string | null;
}

interface SetOwnerDialogProps {
  open: boolean;
  /** What is being given a new owner, e.g. "data contract". */
  noun: string;
  busy: boolean;
  onClose: () => void;
  onSubmit: (userId: number) => void;
}

/**
 * Admin only: makes a Beacon user the owner of a resource, for instance one created before owners were recorded. The
 * owner (and every Admin) may then change, run and delete it. The server checks the user is enabled.
 */
export function SetOwnerDialog({ open, noun, busy, onClose, onSubmit }: SetOwnerDialogProps) {
  const [owner, setOwner] = useState<OwnerCandidate | null>(null);

  return (
    <Modal open={open} onClose={onClose} width={480} ariaLabel={`Change the ${noun}'s owner`}>
      <ModalHeader
        emphasis="Change owner"
        sub={`The owner may change, run and delete this ${noun}. Admins always can.`}
        onClose={onClose}
      />
      <ModalBody>
        <Field label="New owner">
          <SearchPicker<OwnerCandidate>
            path="/beacon/api/users"
            queryKey={['users', 'owner-picker']}
            value={owner?.id}
            selectedLabel={owner ? labelOf(owner) : undefined}
            getId={x => x.id}
            getLabel={labelOf}
            getHint={x => x.email ?? null}
            onSelect={setOwner}
            placeholder="Search users…"
            noun="users"
            ariaLabel="New owner"
          />
        </Field>
      </ModalBody>
      <ModalFooter>
        <Button type="button" onClick={onClose}>Cancel</Button>
        <Button
          variant="primary"
          type="button"
          disabled={owner === null || busy}
          onClick={() => owner && onSubmit(owner.id)}
        >
          Save owner
        </Button>
      </ModalFooter>
    </Modal>
  );
}

function labelOf(user: OwnerCandidate): string {
  return user.displayName ?? user.userName;
}
