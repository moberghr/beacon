import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { describeError, fetchJson } from '@/lib/api';
import { useAuth } from '@/auth/useAuth';
import { Button, Field, Input } from '@/components/beacon';

const SCHEMA = z.object({
  username: z.string().trim().min(1, 'Username is required'),
  password: z.string().min(1, 'Password is required'),
  rememberMe: z.boolean(),
});
type FormValues = z.infer<typeof SCHEMA>;

interface LoginResponse {
  success: boolean;
  error?: string | null;
  redirectUrl?: string | null;
}

export default function LoginPage() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const ssoError = params.get('ssoError');
  const auth = useAuth();
  const [serverError, setServerError] = useState<string | null>(
    ssoError ? 'Single sign-on failed. Please try again or sign in with username and password.' : null,
  );

  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { username: '', password: '', rememberMe: false },
  });

  useEffect(() => {
    if (auth.data?.isAuthenticated) {
      navigate('/home', { replace: true });
    }
  }, [auth.data?.isAuthenticated, navigate]);

  async function onSubmit(values: FormValues) {
    setServerError(null);
    try {
      const result = await fetchJson<LoginResponse>('/beacon/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({
          username: values.username,
          password: values.password,
          rememberMe: values.rememberMe,
        }),
      });
      if (!result.success) {
        setServerError(result.error || 'Invalid username or password.');
        return;
      }
      window.location.href = result.redirectUrl || '/home';
    } catch (e) {
      setServerError(describeError(e, 'Login failed. Try again.'));
    }
  }

  return (
    <AuthShell>
      <h1 className="text-xl font-semibold text-text text-center m-0 mb-2">Sign in</h1>
      <p className="text-text-muted text-center mb-6 m-0">Sign in to your Beacon workspace</p>

      {serverError && <AuthAlert tone="error">{serverError}</AuthAlert>}

      <a className="block" href="/beacon/api/auth/sso/challenge">
        <Button variant="primary" className="w-full justify-center">
          Continue with single sign-on
        </Button>
      </a>

      <AuthDivider />

      <form onSubmit={handleSubmit(onSubmit)} noValidate className="flex flex-col gap-3">
        <Field label="Username">
          <Input type="text" autoComplete="username" disabled={isSubmitting} {...register('username')} />
          {errors.username && <span className="text-xs text-crit">{errors.username.message}</span>}
        </Field>

        <Field label="Password">
          <Input type="password" autoComplete="current-password" disabled={isSubmitting} {...register('password')} />
          {errors.password && <span className="text-xs text-crit">{errors.password.message}</span>}
        </Field>

        <label className="flex items-center gap-2 text-sm text-text-muted">
          <input type="checkbox" {...register('rememberMe')} disabled={isSubmitting} />
          <span>Remember me</span>
        </label>

        <Button
          type="submit"
          variant="primary"
          className="w-full justify-center mt-2"
          disabled={isSubmitting}
        >
          {isSubmitting ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    </AuthShell>
  );
}

export function AuthShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen grid place-items-center p-6 bg-bg">
      <div className="w-full max-w-md bg-surface border border-border rounded-lg shadow-pop p-8">
        {children}
      </div>
    </div>
  );
}

export function AuthAlert({
  tone,
  children,
}: {
  tone: 'error' | 'info' | 'ok';
  children: React.ReactNode;
}) {
  const toneCls =
    tone === 'error'
      ? 'bg-crit-bg text-crit border-crit/40'
      : tone === 'ok'
        ? 'bg-ok-bg text-ok border-ok/40'
        : 'bg-info-bg text-info border-info/40';
  return (
    <div className={`px-3 py-2.5 rounded-md mb-4 text-sm border ${toneCls}`} role="alert">
      {children}
    </div>
  );
}

export function AuthDivider() {
  return (
    <div className="flex items-center my-5 text-xs text-text-muted gap-3">
      <div className="flex-1 border-t border-border" />
      <span>or</span>
      <div className="flex-1 border-t border-border" />
    </div>
  );
}
