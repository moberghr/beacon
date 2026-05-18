import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { describeError, fetchJson } from '@/lib/api';
import { Button, Field, Input } from '@/components/beacon';
import { AuthShell, AuthAlert } from './LoginPage';

interface SetupStatusResponse {
  isSetupComplete: boolean;
}

const SCHEMA = z.object({
  userName: z.string().trim().min(1, 'Username is required'),
  email: z.string().trim().optional().or(z.literal('')),
  displayName: z.string().trim().optional().or(z.literal('')),
  password: z.string().min(8, 'Password must be at least 8 characters'),
  confirmPassword: z.string().min(1, 'Please confirm the password'),
}).refine(v => v.password === v.confirmPassword, {
  path: ['confirmPassword'],
  message: 'Passwords do not match',
});
type FormValues = z.infer<typeof SCHEMA>;

export default function SetupPage() {
  const [status, setStatus] = useState<'loading' | 'open' | 'already' | 'done' | 'error'>('loading');
  const [serverError, setServerError] = useState<string | null>(null);

  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { userName: '', email: '', displayName: '', password: '', confirmPassword: '' },
  });

  useEffect(() => {
    (async () => {
      try {
        const r = await fetchJson<SetupStatusResponse>('/beacon/api/setup/status');
        setStatus(r.isSetupComplete ? 'already' : 'open');
      } catch {
        setStatus('error');
      }
    })();
  }, []);

  async function onSubmit(values: FormValues) {
    setServerError(null);
    try {
      await fetchJson<unknown>('/beacon/api/setup/superadmin', {
        method: 'POST',
        body: JSON.stringify({
          userName: values.userName,
          email: values.email || null,
          displayName: values.displayName || null,
          password: values.password,
          confirmPassword: values.confirmPassword,
        }),
      });
      setStatus('done');
    } catch (e) {
      setServerError(describeError(e, 'Setup failed. Try again.'));
    }
  }

  return (
    <AuthShell>
      <h1 className="text-xl font-semibold text-text text-center m-0 mb-4">First-time setup</h1>

      {status === 'loading' && <p className="text-text-muted text-center m-0">Checking setup status…</p>}

      {status === 'error' && (
        <AuthAlert tone="error">
          Couldn&rsquo;t reach the setup endpoint. Refresh the page.
        </AuthAlert>
      )}

      {status === 'already' && (
        <>
          <AuthAlert tone="info">This Beacon installation has already been set up.</AuthAlert>
          <a href="/login" className="block">
            <Button variant="primary" className="w-full justify-center">Go to sign in</Button>
          </a>
        </>
      )}

      {status === 'done' && (
        <>
          <AuthAlert tone="ok">Super admin account created. You can now sign in.</AuthAlert>
          <a href="/login" className="block">
            <Button variant="primary" className="w-full justify-center">Go to sign in</Button>
          </a>
        </>
      )}

      {status === 'open' && (
        <>
          <p className="text-text-muted text-center mb-4 m-0">
            Create the super admin account to get started.
          </p>

          {serverError && <AuthAlert tone="error">{serverError}</AuthAlert>}

          <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-3">
            <Field label={<>Username <span className="text-crit">*</span></>}>
              <Input type="text" disabled={isSubmitting} {...register('userName')} />
              {errors.userName && <span className="text-xs text-crit">{errors.userName.message}</span>}
            </Field>
            <Field label="Email (optional)">
              <Input type="email" disabled={isSubmitting} {...register('email')} />
            </Field>
            <Field label="Display name (optional)">
              <Input type="text" disabled={isSubmitting} {...register('displayName')} />
            </Field>
            <Field label={<>Password <span className="text-crit">*</span></>}>
              <Input type="password" autoComplete="new-password" disabled={isSubmitting} {...register('password')} />
              {errors.password && <span className="text-xs text-crit">{errors.password.message}</span>}
            </Field>
            <Field label={<>Confirm password <span className="text-crit">*</span></>}>
              <Input type="password" autoComplete="new-password" disabled={isSubmitting} {...register('confirmPassword')} />
              {errors.confirmPassword && <span className="text-xs text-crit">{errors.confirmPassword.message}</span>}
            </Field>

            <Button
              type="submit"
              variant="primary"
              className="w-full justify-center mt-2"
              disabled={isSubmitting}
            >
              {isSubmitting ? 'Creating…' : 'Create super admin'}
            </Button>
          </form>
        </>
      )}
    </AuthShell>
  );
}
