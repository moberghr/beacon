import { useEffect, useState } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { useQuery } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { User, Lock, ShieldCheck } from 'lucide-react';
import { ApiError, describeError, fetchJson } from '@/lib/api';
import { useAuth } from '@/auth/useAuth';
import {
  AuthLayout,
  AuthAlert,
  EmphasisWord,
  AuthLabel,
  AuthField,
  AuthFieldError,
  AuthSubmit,
  AuthSpinner,
} from './AuthLayout';

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
  // SSO is only offered when the backend has OIDC configured. The flag is read pre-auth
  // (anonymous endpoint); the button stays hidden until it resolves true, so we never show a
  // link that would 404 against an SSO-disabled deployment.
  const ssoConfig = useQuery({
    queryKey: ['auth', 'sso-config'],
    queryFn: () => fetchJson<{ enabled: boolean }>('/beacon/api/auth/sso'),
    staleTime: Infinity,
  });
  const ssoEnabled = ssoConfig.data?.enabled === true;
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

      {ssoEnabled && (
        <>
          <div className="mb-4 grid grid-cols-1 gap-2">
            <a
              href="/beacon/api/auth/sso/challenge"
              className="inline-flex items-center justify-center gap-2.5 rounded-sm border border-border-strong bg-surface px-3 py-2.5 text-[13.5px] font-medium text-text no-underline transition-colors hover:border-text-subtle hover:bg-surface-2"
            >
              <ShieldCheck size={16} />
              Continue with single sign-on
            </a>
          </div>

          <div className="relative my-5 flex items-center gap-3 text-xs font-medium uppercase tracking-wide text-text-subtle">
            <span className="h-px flex-1 bg-border" />
            <span>or sign in with username</span>
            <span className="h-px flex-1 bg-border" />
          </div>
        </>
      )}

      <form className="flex flex-col gap-3.5" onSubmit={handleSubmit(onSubmit)} noValidate autoComplete="on">
        <label className="flex flex-col gap-1.5">
          <AuthLabel>Username</AuthLabel>
          <AuthField
            icon={<User size={14} />}
            type="text"
            autoComplete="username"
            placeholder="you@moberg.hr"
            disabled={isSubmitting}
            {...register('username')}
          />
          {errors.username && <AuthFieldError>{errors.username.message}</AuthFieldError>}
        </label>

        <label className="flex flex-col gap-1.5">
          <AuthLabel>Password</AuthLabel>
          <AuthField
            icon={<Lock size={14} />}
            type={showPassword ? 'text' : 'password'}
            autoComplete="current-password"
            placeholder="••••••••"
            disabled={isSubmitting}
            reveal={{ shown: showPassword, onToggle: () => setShowPassword((s) => !s) }}
            {...register('password')}
          />
          {errors.password && <AuthFieldError>{errors.password.message}</AuthFieldError>}
        </label>

        <AuthSubmit disabled={isSubmitting}>
          {isSubmitting ? (
            <>
              <AuthSpinner /> Verifying…
            </>
          ) : (
            <>
              Sign in
              <span className="rounded-xs border border-white/25 bg-white/[0.18] px-1.5 py-px mono text-2xs text-white">
                ↵
              </span>
            </>
          )}
        </AuthSubmit>
      </form>
    </AuthLayout>
  );
}
