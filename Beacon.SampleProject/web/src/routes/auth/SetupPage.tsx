import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { fetchJson, ApiError } from '@/lib/api';
import { AUTH_STYLES } from './LoginPage';

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
      setServerError(e instanceof ApiError ? e.body || e.message : 'Setup failed. Try again.');
    }
  }

  return (
    <div className="auth-shell">
      <div className="auth-card">
        <h1 className="auth-title">First-time setup</h1>

        {status === 'loading' && <p className="muted" style={{ textAlign: 'center' }}>Checking setup status…</p>}

        {status === 'error' && (
          <div className="auth-alert auth-alert--error" role="alert">
            Couldn&rsquo;t reach the setup endpoint. Refresh the page.
          </div>
        )}

        {status === 'already' && (
          <>
            <div className="auth-alert auth-alert--info">This Beacon installation has already been set up.</div>
            <a className="btn btn--primary" href="/app/login" style={{ width: '100%', justifyContent: 'center' }}>
              Go to sign in
            </a>
          </>
        )}

        {status === 'done' && (
          <>
            <div className="auth-alert auth-alert--ok">
              Super admin account created. You can now sign in.
            </div>
            <a className="btn btn--primary" href="/app/login" style={{ width: '100%', justifyContent: 'center' }}>
              Go to sign in
            </a>
          </>
        )}

        {status === 'open' && (
          <>
            <p className="muted" style={{ textAlign: 'center', marginBottom: 16 }}>
              Create the super admin account to get started.
            </p>

            {serverError && <div className="auth-alert auth-alert--error" role="alert">{serverError}</div>}

            <form onSubmit={handleSubmit(onSubmit)} noValidate>
              <label className="auth-field">
                <span>Username *</span>
                <input className="input" type="text" disabled={isSubmitting} {...register('userName')} />
                {errors.userName && <span className="auth-error">{errors.userName.message}</span>}
              </label>
              <label className="auth-field">
                <span>Email (optional)</span>
                <input className="input" type="email" disabled={isSubmitting} {...register('email')} />
              </label>
              <label className="auth-field">
                <span>Display name (optional)</span>
                <input className="input" type="text" disabled={isSubmitting} {...register('displayName')} />
              </label>
              <label className="auth-field">
                <span>Password *</span>
                <input className="input" type="password" autoComplete="new-password" disabled={isSubmitting} {...register('password')} />
                {errors.password && <span className="auth-error">{errors.password.message}</span>}
              </label>
              <label className="auth-field">
                <span>Confirm password *</span>
                <input className="input" type="password" autoComplete="new-password" disabled={isSubmitting} {...register('confirmPassword')} />
                {errors.confirmPassword && <span className="auth-error">{errors.confirmPassword.message}</span>}
              </label>

              <button
                type="submit"
                className="btn btn--primary"
                style={{ width: '100%', justifyContent: 'center', marginTop: 16 }}
                disabled={isSubmitting}
              >
                {isSubmitting ? 'Creating…' : 'Create super admin'}
              </button>
            </form>
          </>
        )}
      </div>
      <style>{AUTH_STYLES}</style>
    </div>
  );
}
