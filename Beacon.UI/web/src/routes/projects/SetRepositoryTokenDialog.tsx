import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input } from '@/components/beacon';
import { describeError } from '@/lib/api';
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
            toast.error(describeError(err, 'Update failed'));
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
          <Button type="button" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button type="submit" form="set-repo-token-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : 'Save token'}
          </Button>
        </>
      }
    >
      <form id="set-repo-token-form" onSubmit={onSubmit} noValidate>
        <Field
          label={<>Personal access token <span className="text-crit">*</span></>}
          hint="Used to scan repository contents. Beacon encrypts the token before storing it."
        >
          <Input
            id="repo-token"
            type="password"
            autoComplete="off"
            aria-invalid={!!errors.accessToken}
            {...register('accessToken')}
          />
          {errors.accessToken && <span className="text-xs text-crit">{errors.accessToken.message}</span>}
        </Field>
      </form>
    </Dialog>
  );
}

export default SetRepositoryTokenDialog;
