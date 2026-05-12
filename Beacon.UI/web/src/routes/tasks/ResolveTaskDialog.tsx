import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Textarea } from '@/components/beacon';
import { ApiError } from '@/lib/api';
import { useResolveTask } from './queries';

const SCHEMA = z.object({
  resolutionNotes: z.string().trim().max(2000, 'Max 2000 characters').optional(),
});

type FormValues = z.infer<typeof SCHEMA>;

interface ResolveTaskDialogProps {
  open: boolean;
  taskId: number | null;
  onClose: () => void;
}

export function ResolveTaskDialog({ open, taskId, onClose }: ResolveTaskDialogProps) {
  const resolve = useResolveTask();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { resolutionNotes: '' },
  });

  useEffect(() => {
    if (open) reset({ resolutionNotes: '' });
  }, [open, reset]);

  const onSubmit = handleSubmit(async values => {
    if (taskId == null) return;
    try {
      await resolve.mutateAsync({
        id: taskId,
        resolutionNotes: values.resolutionNotes?.trim() ? values.resolutionNotes.trim() : null,
      });
      toast.success('Task resolved');
      onClose();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Resolve failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Resolve task"
      sub="Mark this task as resolved. Optionally add notes documenting the resolution."
      size="md"
      footer={
        <>
          <Button onClick={onClose} disabled={isSubmitting}>Cancel</Button>
          <Button type="submit" form="resolve-task-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Resolving…' : 'Resolve task'}
          </Button>
        </>
      }
    >
      <form id="resolve-task-form" onSubmit={onSubmit} noValidate>
        <Field label="Resolution notes">
          <Textarea
            id="resolution-notes"
            rows={5}
            placeholder="Describe how this issue was resolved (optional)…"
            aria-invalid={!!errors.resolutionNotes}
            {...register('resolutionNotes')}
          />
          {errors.resolutionNotes && <span className="text-xs text-crit">{errors.resolutionNotes.message}</span>}
        </Field>
      </form>
    </Dialog>
  );
}
