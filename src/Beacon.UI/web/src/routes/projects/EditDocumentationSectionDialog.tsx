import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Textarea } from '@/components/beacon';
import { useUpdateDocumentationSection, type ProjectDocSectionEntry } from './queries';

const SCHEMA = z.object({
  content: z.string().max(50_000),
});

type FormValues = z.infer<typeof SCHEMA>;

interface Props {
  open: boolean;
  onClose: () => void;
  section: ProjectDocSectionEntry | null;
  projectId: number;
}

export function EditDocumentationSectionDialog({ open, onClose, section, projectId }: Props) {
  const update = useUpdateDocumentationSection();

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { content: '' },
  });

  useEffect(() => {
    if (open && section) {
      reset({ content: section.content ?? '' });
    }
  }, [open, section, reset]);

  const onSubmit = handleSubmit(async values => {
    if (section == null) return;
    try {
      await update.mutateAsync({ sectionId: section.id, content: values.content, projectId });
      toast.success(`Updated section '${section.title}'`);
      onClose();
    } catch {
      // Error toast already raised by the mutation hook.
    }
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={section ? `Edit section: ${section.title}` : 'Edit section'}
      sub="Markdown supported. Mermaid diagrams in ```mermaid code fences are sanitized server-side."
      size="lg"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" form="edit-doc-section-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </Button>
        </>
      }
    >
      <form id="edit-doc-section-form" onSubmit={onSubmit} noValidate>
        <Field label="Content">
          <Textarea
            id="doc-section-content"
            rows={20}
            className="mono text-xs"
            aria-invalid={!!errors.content}
            {...register('content')}
          />
          {errors.content && <span className="text-xs text-crit">{errors.content.message}</span>}
        </Field>
      </form>
    </Dialog>
  );
}

export default EditDocumentationSectionDialog;
