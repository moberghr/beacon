import { useEffect, useState } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { User, Lock, ShieldCheck } from 'lucide-react';
import { ApiError, describeError, fetchJson } from '@/lib/api';
import { useAuth } from '@/auth/useAuth';
import { AuthLayout, AuthAlert, EmphasisWord } from './AuthLayout';

const SCHEMA = z.object({
  username: z.string().trim().min(1, 'Username is required'),
  password: z.string().min(1, 'Password is required'),
});
type FormValues = z.infer<typeof SCHEMA>;

interface LoginResponse {
  success: boolean;
  error?: string | null;
  redirectUrl?: string | null;
}

/**
 * Guards a redirect target against open-redirect: only same-origin relative
 * paths are allowed. `//evil.com`, `/\evil.com` (browsers treat `\` as `/`),
 * and `https://evil.com` are rejected.
 */
function safeRelativePath(path: string | null | undefined): string | null {
  if (typeof path !== 'string') return null;
  if (!path.startsWith('/') || path.startsWith('//') || path.startsWith('/\\')) return null;
  return path;
}

export default function LoginPage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const ssoError = params.get('ssoError');
  const returnTo = safeRelativePath(
    (location.state as { returnTo?: string } | null)?.returnTo,
  );
  const auth = useAuth();
  const [serverError, setServerError] = useState<string | null>(
    ssoError ? 'Single sign-on failed. Please try again or sign in with username and password.' : null,
  );
  const [showPassword, setShowPassword] = useState(false);

  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { username: '', password: '' },
  });

  useEffect(() => {
    if (auth.data?.isAuthenticated) {
      navigate(returnTo ?? '/home', { replace: true });
    }
  }, [auth.data?.isAuthenticated, navigate, returnTo]);

  async function onSubmit(values: FormValues) {
    setServerError(null);
    try {
      const result = await fetchJson<LoginResponse>('/beacon/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({
          username: values.username,
          password: values.password,
          rememberMe: false,
        }),
      });
      if (!result.success) {
        setServerError(result.error || 'Invalid username or password.');
        return;
      }
      // Prefer the deep-link the user originally requested, then the
      // server-supplied redirect, then home — both guarded to same-origin
      // relative paths to block open-redirect.
      window.location.href = returnTo ?? safeRelativePath(result.redirectUrl) ?? '/home';
    } catch (e) {
      setServerError(extractLoginError(e));
    }
  }

  // The login endpoint returns `LoginResponse` JSON with status 401 on
  // bad creds; fetchJson throws ApiError(401, bodyJson) for non-2xx, so
  // err.body is the raw JSON. Parse it to surface the human-readable
  // `error` field instead of the wire payload.
  function extractLoginError(e: unknown): string {
    if (e instanceof ApiError) {
      try {
        const parsed = JSON.parse(e.body) as Partial<LoginResponse>;
        if (typeof parsed.error === 'string' && parsed.error.length > 0) {
          return parsed.error;
        }
      } catch {
        // body wasn't JSON — fall through to describeError
      }
    }
    return describeError(e, 'Login failed. Try again.');
  }

  return (
    <AuthLayout
      eyebrow="SIGN IN"
      title={
        <>
          Welcome <EmphasisWord>back</EmphasisWord>.
        </>
      }
      subtitle="Sign in to your Beacon workspace."
    >
      {serverError && <AuthAlert tone="error">{serverError}</AuthAlert>}

      <div className="login__sso">
        <a href="/beacon/api/auth/sso/challenge" className="login__sso-btn">
          <ShieldCheck size={16} />
          Continue with single sign-on
        </a>
      </div>

      <div className="login__divider">
        <span>or sign in with username</span>
      </div>

      <form className="login__form" onSubmit={handleSubmit(onSubmit)} noValidate autoComplete="on">
        <label className="login__field">
          <span className="login__label">Username</span>
          <div className="login__input">
            <User size={14} className="login__input-icon" />
            <input
              type="text"
              autoComplete="username"
              placeholder="you@moberg.hr"
              disabled={isSubmitting}
              {...register('username')}
            />
          </div>
          {errors.username && <span className="login__field-error">{errors.username.message}</span>}
        </label>

        <label className="login__field">
          <span className="login__label">Password</span>
          <div className="login__input">
            <Lock size={14} className="login__input-icon" />
            <input
              type={showPassword ? 'text' : 'password'}
              autoComplete="current-password"
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

        <button type="submit" className="login__submit" disabled={isSubmitting}>
          {isSubmitting ? (
            <>
              <span className="login__spinner" /> Verifying…
            </>
          ) : (
            <>
              Sign in
              <span className="login__submit-kbd">
                <span className="kbd">↵</span>
              </span>
            </>
          )}
        </button>
      </form>
    </AuthLayout>
  );
}
