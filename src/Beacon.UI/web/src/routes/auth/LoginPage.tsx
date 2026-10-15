import { useEffect, useState } from 'react';
import { useHref, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { useQuery } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { User, Lock, ShieldCheck, KeyRound } from 'lucide-react';
import { ApiError, describeError, fetchJson } from '@/lib/api';
// The login answer as the API declares it; passwordChangeRequired: the credentials were right, but an administrator set
// the password, so a new one is sent as newPassword.
import type { LoginResponse } from '@/api/generated/beacon-api';
import { useAuth } from '@/auth/useAuth';
import { redirectToExternalLogin } from '@/auth/externalLogin';
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

const NEW_PASSWORD_SCHEMA = z
  .object({
    newPassword: z.string().min(1, 'Choose a new password'),
    confirmPassword: z.string().min(1, 'Repeat the new password'),
  })
  .refine((x) => x.newPassword === x.confirmPassword, {
    message: 'The passwords do not match',
    path: ['confirmPassword'],
  });
type NewPasswordValues = z.infer<typeof NEW_PASSWORD_SCHEMA>;

// Whether SSO is offered, and whether the password form is open to everyone (false: only break-glass accounts sign
// in with a password, through /login?local=1).
interface SsoConfig {
  enabled: boolean;
  localLoginEnabled?: boolean;
}

// The credentials a user whose password an administrator set signed in with, kept until they choose a new one.
interface PendingChange {
  username: string;
  password: string;
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
  // Both ways out of this page bypass the router (the host redirecting back after its own login, or
  // the full page load after a local sign-in), so the target needs the router basename: useHref adds it.
  const returnHref = useHref(returnTo ?? '/home');
  // A host that owns sign-in never shows this page: send the user to the host's login instead.
  const externalLogin = auth.data && !auth.data.isAuthenticated ? auth.data.externalLogin : null;
  useEffect(() => {
    if (externalLogin) {
      redirectToExternalLogin(externalLogin, returnHref);
    }
  }, [externalLogin, returnHref]);
  // SSO is only offered when the backend has OIDC configured. The flag is read pre-auth
  // (anonymous endpoint); the button stays hidden until it resolves true, so we never show a
  // link that would 404 against an SSO-disabled deployment.
  const ssoConfig = useQuery({
    queryKey: ['auth', 'sso-config'],
    queryFn: () => fetchJson<SsoConfig>('/beacon/api/auth/sso'),
    staleTime: Infinity,
  });
  const ssoEnabled = ssoConfig.data?.enabled === true;
  // The password form waits for the configuration, so a deployment without password sign-in never flashes it. A
  // break-glass account opens it with ?local=1; the server decides who may use it.
  const configSettled = !ssoConfig.isLoading;
  const passwordFormShown =
    configSettled && (ssoConfig.data?.localLoginEnabled !== false || params.get('local') === '1');
  const [serverError, setServerError] = useState<string | null>(
    ssoError === 'not_admitted'
      ? 'Your account is not permitted to use Beacon. Ask an administrator to grant you access.'
      : ssoError === 'mfa_required'
        ? 'Your sign-in did not use multi-factor authentication. Sign in again and complete the second step your organisation requires.'
        : ssoError
          ? 'Single sign-on failed. Please try again or sign in with username and password.'
          : null,
  );
  const [showPassword, setShowPassword] = useState(false);
  const [pendingChange, setPendingChange] = useState<PendingChange | null>(null);

  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { username: '', password: '' },
  });
  const newPasswordForm = useForm<NewPasswordValues>({
    resolver: zodResolver(NEW_PASSWORD_SCHEMA),
    defaultValues: { newPassword: '', confirmPassword: '' },
  });

  useEffect(() => {
    if (auth.data?.isAuthenticated) {
      navigate(returnTo ?? '/home', { replace: true });
    }
  }, [auth.data?.isAuthenticated, navigate, returnTo]);

  async function signIn(credentials: PendingChange, newPassword?: string) {
    setServerError(null);
    try {
      const result = await fetchJson<LoginResponse>('/beacon/api/auth/login', {
        method: 'POST',
        body: JSON.stringify({
          username: credentials.username,
          password: credentials.password,
          rememberMe: false,
          ...(newPassword ? { newPassword } : {}),
        }),
      });
      if (!result.success) {
        setServerError(result.error || 'Invalid username or password.');
        return;
      }
      // Prefer the deep-link the user originally requested, then the
      // server-supplied redirect (already a full path), then home — both
      // guarded to same-origin relative paths to block open-redirect.
      window.location.href = returnTo ? returnHref : safeRelativePath(result.redirectUrl) ?? returnHref;
    } catch (e) {
      const response = loginResponseOf(e);
      if (response?.passwordChangeRequired) {
        // The right credentials, but the password was set by an administrator: the user chooses their own.
        setPendingChange(credentials);
        setServerError(newPassword ? response.error || 'Choose another password.' : null);
        return;
      }
      setPendingChange(null);
      setServerError(response?.error || describeError(e, 'Login failed. Try again.'));
    }
  }

  function onSubmit(values: FormValues) {
    return signIn({ username: values.username, password: values.password });
  }

  function onChoosePassword(values: NewPasswordValues) {
    return pendingChange ? signIn(pendingChange, values.newPassword) : Promise.resolve();
  }

  // The login endpoint returns `LoginResponse` JSON with status 401 on
  // bad creds (400 for a new password it refuses); fetchJson throws
  // ApiError(status, bodyJson) for non-2xx, so err.body is the raw JSON.
  function loginResponseOf(e: unknown): Partial<LoginResponse> | null {
    if (!(e instanceof ApiError)) {
      return null;
    }
    try {
      return JSON.parse(e.body) as Partial<LoginResponse>;
    } catch {
      // body wasn't JSON — the caller falls back to describeError
      return null;
    }
  }

  // Nothing is rendered until /auth/me answers, so an external-login host never flashes this form.
  if (auth.isLoading || externalLogin) {
    return (
      <div className="grid place-items-center h-full">
        <span className="text-text-muted text-sm">{externalLogin ? 'Redirecting to sign in…' : 'Loading…'}</span>
      </div>
    );
  }

  if (pendingChange) {
    const changeErrors = newPasswordForm.formState.errors;
    const changing = newPasswordForm.formState.isSubmitting;
    return (
      <AuthLayout
        eyebrow="NEW PASSWORD"
        title={
          <>
            Choose your <EmphasisWord>own</EmphasisWord> password.
          </>
        }
        subtitle="An administrator set your current password. Choose a new one to finish signing in."
      >
        {serverError && <AuthAlert tone="error">{serverError}</AuthAlert>}

        <form
          className="flex flex-col gap-3.5"
          onSubmit={newPasswordForm.handleSubmit(onChoosePassword)}
          noValidate
          autoComplete="off"
        >
          <label className="flex flex-col gap-1.5">
            <AuthLabel>New password</AuthLabel>
            <AuthField
              icon={<KeyRound size={14} />}
              type={showPassword ? 'text' : 'password'}
              autoComplete="new-password"
              disabled={changing}
              reveal={{ shown: showPassword, onToggle: () => setShowPassword((s) => !s) }}
              {...newPasswordForm.register('newPassword')}
            />
            {changeErrors.newPassword && <AuthFieldError>{changeErrors.newPassword.message}</AuthFieldError>}
          </label>

          <label className="flex flex-col gap-1.5">
            <AuthLabel>Repeat new password</AuthLabel>
            <AuthField
              icon={<KeyRound size={14} />}
              type={showPassword ? 'text' : 'password'}
              autoComplete="new-password"
              disabled={changing}
              {...newPasswordForm.register('confirmPassword')}
            />
            {changeErrors.confirmPassword && <AuthFieldError>{changeErrors.confirmPassword.message}</AuthFieldError>}
          </label>

          <AuthSubmit disabled={changing}>
            {changing ? (
              <>
                <AuthSpinner /> Saving…
              </>
            ) : (
              'Set password and sign in'
            )}
          </AuthSubmit>
        </form>
      </AuthLayout>
    );
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

      {configSettled && !passwordFormShown && !ssoEnabled && (
        <AuthAlert tone="info">
          Password sign-in is turned off for this deployment. Ask an administrator how to sign in.
        </AuthAlert>
      )}

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

          {passwordFormShown && (
            <div className="relative my-5 flex items-center gap-3 text-xs font-medium uppercase tracking-wide text-text-subtle">
              <span className="h-px flex-1 bg-border" />
              <span>or sign in with username</span>
              <span className="h-px flex-1 bg-border" />
            </div>
          )}
        </>
      )}

      {passwordFormShown && (
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
      )}
    </AuthLayout>
  );
}
