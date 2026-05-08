import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { fetchJson, ApiError } from '@/lib/api';
import { useAuth } from '@/auth/useAuth';

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

  // If already authenticated, redirect to /home (inside /app)
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
      // Force full reload so the new auth cookie applies
      window.location.href = result.redirectUrl || '/app/home';
    } catch (e) {
      setServerError(e instanceof ApiError ? e.body || e.message : 'Login failed. Try again.');
    }
  }

  return (
    <div className="auth-shell">
      <div className="auth-card">
        <h1 className="auth-title">Sign in</h1>
        <p className="muted" style={{ textAlign: 'center', marginBottom: 24 }}>
          Sign in to your Beacon workspace
        </p>

        {serverError && (
          <div className="auth-alert auth-alert--error" role="alert">{serverError}</div>
        )}

        <a className="btn btn--primary" href="/beacon/api/auth/sso/challenge" style={{ width: '100%', justifyContent: 'center' }}>
          Continue with single sign-on
        </a>

        <div className="auth-divider"><span>or</span></div>

        <form onSubmit={handleSubmit(onSubmit)} noValidate>
          <label className="auth-field">
            <span>Username</span>
            <input className="input" type="text" autoComplete="username" disabled={isSubmitting} {...register('username')} />
            {errors.username && <span className="auth-error">{errors.username.message}</span>}
          </label>

          <label className="auth-field">
            <span>Password</span>
            <input className="input" type="password" autoComplete="current-password" disabled={isSubmitting} {...register('password')} />
            {errors.password && <span className="auth-error">{errors.password.message}</span>}
          </label>

          <label className="auth-checkbox">
            <input type="checkbox" {...register('rememberMe')} disabled={isSubmitting} />
            <span>Remember me</span>
          </label>

          <button
            type="submit"
            className="btn btn--primary"
            style={{ width: '100%', justifyContent: 'center', marginTop: 16 }}
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>

      <style>{AUTH_STYLES}</style>
    </div>
  );
}

const AUTH_STYLES = `
  .auth-shell {
    min-height: 100vh;
    display: grid;
    place-items: center;
    padding: 24px;
    background: var(--bg, #0f1423);
  }
  .auth-card {
    width: 100%;
    max-width: 420px;
    background: var(--surface, rgba(15, 20, 35, 0.85));
    border: 1px solid var(--border, rgba(255, 255, 255, 0.08));
    border-radius: 16px;
    padding: 32px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
  }
  .auth-title {
    font-size: 22px;
    margin: 0 0 8px;
    text-align: center;
    color: var(--text);
  }
  .auth-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-bottom: 12px;
  }
  .auth-field > span {
    font-size: 13px;
    color: var(--muted);
  }
  .auth-error {
    font-size: 12px;
    color: var(--crit, #c00);
    margin-top: 2px;
  }
  .auth-checkbox {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 13px;
    color: var(--muted);
    margin: 8px 0 0;
  }
  .auth-divider {
    display: flex;
    align-items: center;
    margin: 20px 0;
    color: var(--muted);
    font-size: 12px;
  }
  .auth-divider::before, .auth-divider::after {
    content: '';
    flex: 1;
    border-top: 1px solid var(--border);
  }
  .auth-divider > span {
    padding: 0 12px;
  }
  .auth-alert {
    padding: 10px 12px;
    border-radius: 8px;
    margin-bottom: 16px;
    font-size: 13px;
  }
  .auth-alert--error {
    background: rgba(220, 38, 38, 0.12);
    border: 1px solid rgba(220, 38, 38, 0.4);
    color: #fca5a5;
  }
  .auth-alert--info {
    background: rgba(59, 130, 246, 0.12);
    border: 1px solid rgba(59, 130, 246, 0.4);
    color: #93c5fd;
  }
  .auth-alert--ok {
    background: rgba(16, 185, 129, 0.12);
    border: 1px solid rgba(16, 185, 129, 0.4);
    color: #6ee7b7;
  }
`;

export { AUTH_STYLES };
