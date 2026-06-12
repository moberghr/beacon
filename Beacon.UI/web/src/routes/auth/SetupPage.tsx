import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { User, Mail, IdCard, Lock, ShieldCheck, ArrowRight } from 'lucide-react';
import { describeError, fetchJson } from '@/lib/api';
import { AuthLayout, AuthAlert, EmphasisWord } from './AuthLayout';

interface SetupStatusResponse {
  isSetupComplete: boolean;
}

const SCHEMA = z
  .object({
    userName: z.string().trim().min(1, 'Username is required'),
    email: z.union([z.literal(''), z.email('Invalid email').max(200)]).optional(),
    displayName: z.string().trim().optional().or(z.literal('')),
    password: z.string().min(8, 'Password must be at least 8 characters'),
    confirmPassword: z.string().min(1, 'Please confirm the password'),
  })
  .refine((v) => v.password === v.confirmPassword, {
    path: ['confirmPassword'],
    message: 'Passwords do not match',
  });
type FormValues = z.infer<typeof SCHEMA>;

export default function SetupPage() {
  const [status, setStatus] = useState<'loading' | 'open' | 'already' | 'done' | 'error'>('loading');
  const [serverError, setServerError] = useState<string | null>(null);
  const [showPassword, setShowPassword] = useState(false);

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

  const title =
    status === 'done' ? (
      <>
        Setup <EmphasisWord>complete</EmphasisWord>.
      </>
    ) : status === 'already' ? (
      <>
        Already <EmphasisWord>set up</EmphasisWord>.
      </>
    ) : status === 'error' ? (
      <>
        Couldn't <EmphasisWord>reach</EmphasisWord> setup.
      </>
    ) : (
      <>
        First <EmphasisWord>run</EmphasisWord>.
      </>
    );

  const subtitle =
    status === 'done'
      ? 'Your super admin account is ready. Sign in to start watching.'
      : status === 'already'
        ? 'This Beacon installation already has a super admin.'
        : status === 'error'
          ? 'The setup endpoint is unreachable. Refresh the page once the server is up.'
          : status === 'loading'
            ? 'Checking setup status…'
            : 'Create the super admin account that will own this Beacon workspace.';

  return (
    <AuthLayout
      eyebrow={status === 'done' ? 'READY' : 'FIRST RUN'}
      title={title}
      subtitle={subtitle}
      leadTitle={
        <>
          Bring the beacon <EmphasisWord>online</EmphasisWord>.
        </>
      }
      leadSub={
        <>
          One account, one source of truth. The super admin owns provisioning, integrations and the
          audit trail.
        </>
      }
    >
      {status === 'error' && (
        <AuthAlert tone="error">Couldn't reach the setup endpoint. Refresh the page.</AuthAlert>
      )}

      {(status === 'already' || status === 'done') && (
        <>
          <AuthAlert tone={status === 'done' ? 'ok' : 'info'}>
            {status === 'done'
              ? 'Super admin account created. You can now sign in.'
              : 'This Beacon installation has already been set up.'}
          </AuthAlert>
          <a
            href="/login"
            className="login__submit"
            style={{ textDecoration: 'none', marginTop: 16 }}
          >
            Go to sign in
            <ArrowRight size={14} />
          </a>
        </>
      )}

      {status === 'open' && (
        <>
          {serverError && <AuthAlert tone="error">{serverError}</AuthAlert>}

          <form className="login__form" onSubmit={handleSubmit(onSubmit)} noValidate autoComplete="on">
            <label className="login__field">
              <span className="login__label">Username *</span>
              <div className="login__input">
                <User size={14} className="login__input-icon" />
                <input
                  type="text"
                  autoComplete="username"
                  placeholder="admin"
                  disabled={isSubmitting}
                  {...register('userName')}
                />
              </div>
              {errors.userName && <span className="login__field-error">{errors.userName.message}</span>}
            </label>

            <label className="login__field">
              <span className="login__label">Email · optional</span>
              <div className="login__input">
                <Mail size={14} className="login__input-icon" />
                <input
                  type="email"
                  autoComplete="email"
                  placeholder="admin@moberg.hr"
                  disabled={isSubmitting}
                  {...register('email')}
                />
              </div>
              {errors.email && <span className="login__field-error">{errors.email.message}</span>}
            </label>

            <label className="login__field">
              <span className="login__label">Display name · optional</span>
              <div className="login__input">
                <IdCard size={14} className="login__input-icon" />
                <input
                  type="text"
                  autoComplete="name"
                  placeholder="Admin"
                  disabled={isSubmitting}
                  {...register('displayName')}
                />
              </div>
            </label>

            <label className="login__field">
              <span className="login__label-row">
                <span className="login__label">Password *</span>
                <span className="login__forgot" style={{ cursor: 'default' }}>
                  min 8 chars
                </span>
              </span>
              <div className="login__input">
                <Lock size={14} className="login__input-icon" />
                <input
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  placeholder="••••••••"
                  disabled={isSubmitting}
                  {...register('password')}
                />
                <button
                  type="button"
                  className="login__reveal"
                  onClick={() => setShowPassword((s) => !s)}
                  aria-label={showPassword ? 'Hide password' : 'Show password'}
                >
                  {showPassword ? 'Hide' : 'Show'}
                </button>
              </div>
              {errors.password && <span className="login__field-error">{errors.password.message}</span>}
            </label>

            <label className="login__field">
              <span className="login__label">Confirm password *</span>
              <div className="login__input">
                <ShieldCheck size={14} className="login__input-icon" />
                <input
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  placeholder="••••••••"
                  disabled={isSubmitting}
                  {...register('confirmPassword')}
                />
              </div>
              {errors.confirmPassword && (
                <span className="login__field-error">{errors.confirmPassword.message}</span>
              )}
            </label>

            <button type="submit" className="login__submit" disabled={isSubmitting}>
              {isSubmitting ? (
                <>
                  <span className="login__spinner" /> Provisioning…
                </>
              ) : (
                <>
                  Create super admin
                  <ArrowRight size={14} />
                </>
              )}
            </button>
          </form>
        </>
      )}
    </AuthLayout>
  );
}
