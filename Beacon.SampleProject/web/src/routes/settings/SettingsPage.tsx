import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { ApiError } from '@/lib/api';
import {
  useChangeOwnPassword,
  useUserSettingsQuery,
} from './queries';

const PASSWORD_SCHEMA = z
  .object({
    currentPassword: z.string().min(1, 'Current password is required'),
    newPassword: z.string().min(8, 'New password must be at least 8 characters'),
    confirmPassword: z.string().min(1, 'Confirm your new password'),
  })
  .refine(d => d.newPassword === d.confirmPassword, {
    path: ['confirmPassword'],
    message: 'Passwords do not match',
  });

type PasswordFormValues = z.infer<typeof PASSWORD_SCHEMA>;

export default function SettingsPage() {
  const { data, isLoading, isError, error } = useUserSettingsQuery();
  const user = data?.user;

  if (isLoading) {
    return (
      <div className="page">
        <PageHeader title="Settings" sub={<span className="muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="page">
        <PageHeader title="Settings" />
        <EmptyState
          icon={<Icon.Alert size={20} />}
          title="Failed to load account"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (!user) {
    return (
      <div className="page">
        <PageHeader title="Settings" />
        <EmptyState icon={<Icon.Cog size={20} />} title="No account information available" />
      </div>
    );
  }

  return (
    <div className="page">
      <PageHeader title="Settings" sub={<span className="muted">Your account.</span>} />

      <div className="card" style={{ marginBottom: 16 }}>
        <div className="card__body">
          <h3 className="card__title" style={{ margin: '0 0 12px' }}>Account</h3>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: 12 }}>
            <ReadOnly label="Username" value={user.userName} />
            <ReadOnly label="Display name" value={user.displayName ?? '—'} />
            <ReadOnly label="Email" value={user.email ?? '—'} />
            <ReadOnly label="Roles" value={user.roles.length > 0 ? user.roles.join(', ') : '—'} />
          </div>
        </div>
      </div>

      {user.isInternalUser && <ChangePasswordCard />}

      {!user.isInternalUser && (
        <div className="card">
          <div className="card__body">
            <span className="muted">
              Your account is managed by an external identity provider. Password changes happen there.
            </span>
          </div>
        </div>
      )}
    </div>
  );
}

function ReadOnly({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>{label}</div>
      <input className="q-input" type="text" value={value} readOnly />
    </div>
  );
}

function ChangePasswordCard() {
  const changeMutation = useChangeOwnPassword();

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<PasswordFormValues>({
    resolver: zodResolver(PASSWORD_SCHEMA),
    defaultValues: {
      currentPassword: '',
      newPassword: '',
      confirmPassword: '',
    },
  });

  const onSubmit = handleSubmit(async values => {
    try {
      await changeMutation.mutateAsync({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      });
      toast.success('Password changed');
      reset();
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Request failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  return (
    <div className="card">
      <div className="card__body">
        <h3 className="card__title" style={{ margin: '0 0 12px' }}>Change password</h3>
        <form onSubmit={onSubmit} noValidate style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 400 }}>
          <div className="q-field">
            <label className="q-label" htmlFor="cur-pwd">Current password<span className="q-label__req">*</span></label>
            <input
              id="cur-pwd"
              type="password"
              className={`q-input${errors.currentPassword ? ' q-input--error' : ''}`}
              autoComplete="current-password"
              {...register('currentPassword')}
            />
            {errors.currentPassword && <div className="q-error">{errors.currentPassword.message}</div>}
          </div>
          <div className="q-field">
            <label className="q-label" htmlFor="new-pwd">New password<span className="q-label__req">*</span></label>
            <input
              id="new-pwd"
              type="password"
              className={`q-input${errors.newPassword ? ' q-input--error' : ''}`}
              autoComplete="new-password"
              {...register('newPassword')}
            />
            {errors.newPassword && <div className="q-error">{errors.newPassword.message}</div>}
          </div>
          <div className="q-field">
            <label className="q-label" htmlFor="conf-pwd">Confirm new password<span className="q-label__req">*</span></label>
            <input
              id="conf-pwd"
              type="password"
              className={`q-input${errors.confirmPassword ? ' q-input--error' : ''}`}
              autoComplete="new-password"
              {...register('confirmPassword')}
            />
            {errors.confirmPassword && <div className="q-error">{errors.confirmPassword.message}</div>}
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <button type="submit" className="btn btn--primary" disabled={isSubmitting}>
              {isSubmitting ? 'Changing…' : 'Change password'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
