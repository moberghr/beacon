import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { AlertTriangle, Settings as SettingsIcon } from 'lucide-react';
import { EmptyState } from '@/components/data/EmptyState';
import { Button, Card, CardBody, Field, Input, PageHeader } from '@/components/beacon';
import { describeError } from '@/lib/api';
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
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="Account" prefix="Your" emphasis="settings" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (isError) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="Account" prefix="Your" emphasis="settings" />
        <EmptyState
          icon={<AlertTriangle />}
          title="Failed to load account"
          description={error instanceof Error ? error.message : 'Unknown error'}
        />
      </div>
    );
  }

  if (!user) {
    return (
      <div className="flex flex-col gap-4 p-7">
        <PageHeader eyebrow="Account" prefix="Your" emphasis="settings" />
        <EmptyState icon={<SettingsIcon />} title="No account information available" />
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-4 p-7">
      <PageHeader eyebrow="Account" prefix="Your" emphasis="settings" sub={<span className="text-text-muted">Your account.</span>} />

      <Card>
        <CardBody>
          <h3 className="m-0 mb-3 text-sm font-semibold text-text">Account</h3>
          <div className="grid grid-cols-2 gap-3">
            <ReadOnly label="Username" value={user.userName} />
            <ReadOnly label="Display name" value={user.displayName ?? '—'} />
            <ReadOnly label="Email" value={user.email ?? '—'} />
            <ReadOnly label="Roles" value={user.roles.length > 0 ? user.roles.join(', ') : '—'} />
          </div>
        </CardBody>
      </Card>

      {user.isInternalUser && <ChangePasswordCard />}

      {!user.isInternalUser && (
        <Card>
          <CardBody>
            <span className="text-text-muted">
              Your account is managed by an external identity provider. Password changes happen there.
            </span>
          </CardBody>
        </Card>
      )}
    </div>
  );
}

function ReadOnly({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="text-text-muted text-xs mb-1">{label}</div>
      <Input type="text" value={value} readOnly />
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
            toast.error(describeError(err, 'Request failed'));
    }
  });

  return (
    <Card>
      <CardBody>
        <h3 className="m-0 mb-3 text-sm font-semibold text-text">Change password</h3>
        <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3 max-w-sm">
          <Field label={<>Current password <span className="text-crit">*</span></>}>
            <Input
              id="cur-pwd"
              type="password"
              autoComplete="current-password"
              aria-invalid={!!errors.currentPassword}
              {...register('currentPassword')}
            />
            {errors.currentPassword && <span className="text-xs text-crit">{errors.currentPassword.message}</span>}
          </Field>
          <Field label={<>New password <span className="text-crit">*</span></>}>
            <Input
              id="new-pwd"
              type="password"
              autoComplete="new-password"
              aria-invalid={!!errors.newPassword}
              {...register('newPassword')}
            />
            {errors.newPassword && <span className="text-xs text-crit">{errors.newPassword.message}</span>}
          </Field>
          <Field label={<>Confirm new password <span className="text-crit">*</span></>}>
            <Input
              id="conf-pwd"
              type="password"
              autoComplete="new-password"
              aria-invalid={!!errors.confirmPassword}
              {...register('confirmPassword')}
            />
            {errors.confirmPassword && <span className="text-xs text-crit">{errors.confirmPassword.message}</span>}
          </Field>
          <div className="flex justify-end">
            <Button type="submit" variant="primary" disabled={isSubmitting}>
              {isSubmitting ? 'Changing…' : 'Change password'}
            </Button>
          </div>
        </form>
      </CardBody>
    </Card>
  );
}
