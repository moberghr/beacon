import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import type { ProjectDocSectionEntry } from '@/api/generated/beacon-api';
import { Dialog } from '@/components/ui/Dialog';
import { ApiError } from '@/lib/api';
import { useUpdateDocumentationSection } from './queries';

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
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Update failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
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
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="edit-doc-section-form"
            className="btn btn--primary"
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </button>
        </>
      }
    >
      <form id="edit-doc-section-form" onSubmit={onSubmit} noValidate>
        <div className="q-field">
          <label className="q-label" htmlFor="doc-section-content">Content</label>
          <textarea
            id="doc-section-content"
            className={`q-textarea${errors.content ? ' q-textarea--error' : ''}`}
            rows={20}
            style={{ fontFamily: 'var(--mono, monospace)', fontSize: 13 }}
            {...register('content')}
          />
          {errors.content && <div className="q-error">{errors.content.message}</div>}
        </div>
      </form>
    </Dialog>
  );
}

export default EditDocumentationSectionDialog;
