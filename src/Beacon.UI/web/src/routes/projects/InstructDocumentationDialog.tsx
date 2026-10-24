import { useEffect, useState } from 'react';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { ConfirmDialog } from '@/components/ui/ConfirmDialog';
import { Button, Field, Textarea } from '@/components/beacon';
import {
  useInstructDocumentationSection,
  useUpdateDocumentationSection,
  type ProjectDocSectionEntry,
} from './queries';

interface Props {
  open: boolean;
  onClose: () => void;
  section: ProjectDocSectionEntry | null;
  projectId: number;
}

/**
 * Asks the LLM to change one section by an instruction. The answer is only a proposal: it is shown, and replaces the
 * section's text only after the caller confirms (the same save as editing the section by hand).
 */
export function InstructDocumentationDialog({ open, onClose, section, projectId }: Props) {
  const instruct = useInstructDocumentationSection();
  const update = useUpdateDocumentationSection();
  const [instruction, setInstruction] = useState('');
  const [proposal, setProposal] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    if (open) {
      setInstruction('');
      setProposal(null);
      setConfirming(false);
    }
  }, [open, section?.id]);

  if (section == null) {
    return null;
  }

  async function propose() {
    if (section == null || instruction.trim() === '') {
      return;
    }
    try {
      setProposal(await instruct.mutateAsync({ sectionId: section.id, instruction: instruction.trim() }));
    } catch {
      // Error toast already raised by the mutation hook.
    }
  }

  async function apply() {
    if (section == null || proposal == null) {
      return;
    }
    try {
      await update.mutateAsync({ sectionId: section.id, content: proposal, projectId });
      toast.success(`Updated section '${section.title}'`);
      setConfirming(false);
      onClose();
    } catch {
      setConfirming(false);
    }
  }

  const proposing = instruct.isPending;

  return (
    <>
      <Dialog
        open={open}
        onClose={onClose}
        title={`Change section: ${section.title}`}
        sub="Describe the change; the proposed text is shown before anything is saved. Its row data is handled by the deployment's LLM settings."
        size="lg"
        footer={
          <>
            <Button type="button" onClick={onClose} disabled={proposing || update.isPending}>
              Cancel
            </Button>
            {proposal == null ? (
              <Button
                type="button"
                variant="primary"
                onClick={() => void propose()}
                disabled={proposing || instruction.trim() === ''}
              >
                {proposing ? 'Asking…' : 'Propose change'}
              </Button>
            ) : (
              <>
                <Button type="button" onClick={() => setProposal(null)} disabled={update.isPending}>
                  Change the instruction
                </Button>
                <Button type="button" variant="primary" onClick={() => setConfirming(true)} disabled={update.isPending}>
                  Apply change
                </Button>
              </>
            )}
          </>
        }
      >
        {proposal == null ? (
          <Field label="Instruction">
            <Textarea
              id="doc-section-instruction"
              rows={5}
              maxLength={4000}
              placeholder="For example: mark the legacy orders table as deprecated."
              value={instruction}
              onChange={event => setInstruction(event.target.value)}
            />
          </Field>
        ) : (
          <Field label="Proposed content">
            <Textarea id="doc-section-proposal" rows={20} className="mono text-xs" readOnly value={proposal} />
          </Field>
        )}
      </Dialog>

      <ConfirmDialog
        open={confirming}
        title="Replace this section?"
        message={`The section '${section.title}' is replaced with the proposed text. It becomes part of what every caller's prompts are grounded on; you can edit it again afterwards.`}
        confirmLabel="Replace section"
        busy={update.isPending}
        onConfirm={() => void apply()}
        onCancel={() => setConfirming(false)}
      />
    </>
  );
}
