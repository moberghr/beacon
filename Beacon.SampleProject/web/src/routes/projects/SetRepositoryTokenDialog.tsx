import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { ApiError } from '@/lib/api';
import { useUpdateRepositoryToken } from './queries';

const SCHEMA = z.object({
  accessToken: z.string().trim().min(1, 'Token is required').max(500),
});

type FormValues = z.infer<typeof SCHEMA>;

interface Props {
  open: boolean;
  onClose: () => void;
  repository: { id: number; name: string } | null;
}

/**
 * One-way token form: tokens are write-only — Beacon never echoes a stored
 * token back. The field is empty on every open.
 */
export function SetRepositoryTokenDialog({ open, onClose, repository }: Props) {
  const update = useUpdateRepositoryToken();

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { accessToken: '' },
  });

  useEffect(() => {
    if (open) reset({ accessToken: '' });
  }, [open, reset]);

  const onSubmit = handleSubmit(async values => {
    if (repository == null) return;
    try {
      await update.mutateAsync({ repoId: repository.id, accessToken: values.accessToken });
      toast.success(`Updated token for '${repository.name}'`);
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
      title="Set repository access token"
      sub={
        repository
          ? `${repository.name} — token is stored encrypted at rest. It is never displayed again after saving.`
          : ''
      }
      size="md"
      footer={
        <>
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="set-repo-token-form"
            className="btn btn--primary"
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Saving…' : 'Save token'}
          </button>
        </>
      }
    >
      <form id="set-repo-token-form" onSubmit={onSubmit} noValidate>
        <div className="q-field">
          <label className="q-label" htmlFor="repo-token">
            Personal access token<span className="q-label__req">*</span>
          </label>
          <input
            id="repo-token"
            type="password"
            autoComplete="off"
            className={`q-input${errors.accessToken ? ' q-input--error' : ''}`}
            {...register('accessToken')}
          />
          <div className="q-help">
            Used to scan repository contents. Beacon encrypts the token before storing it.
          </div>
          {errors.accessToken && <div className="q-error">{errors.accessToken.message}</div>}
        </div>
      </form>
    </Dialog>
  );
}

export default SetRepositoryTokenDialog;
