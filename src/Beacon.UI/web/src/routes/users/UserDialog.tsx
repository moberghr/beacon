import { useEffect, useMemo, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { AlertTriangle, Lock } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { Banner, Button, Field, Input } from '@/components/beacon';
import { useAccess, useAuth } from '@/auth/useAuth';
import { ApiError, describeError, httpErrorInfo } from '@/lib/api';
import { cn } from '@/lib/utils';
import {
  useCreateExternalUser,
  useCreateInternalUser,
  useRolesQuery,
  useSetUserRoles,
  useSignUserOutEverywhere,
  useUpdateUser,
  type RoleEntry,
  type UserEntry,
} from './queries';
import { OwnedResources } from './OwnedResources';
import {
  INTERNAL_USER_NOTE,
  SIGN_IN_IDENTITY_NOTE,
  changeBlockedReason,
  isFunctionalRole,
  mayAssignRole,
} from './userAdministration';

const COMMON = {
  userName: z.string().trim().min(3, 'Min 3 characters').max(100),
  email: z.union([z.literal(''), z.email('Invalid email').max(200)]).optional(),
  displayName: z.string().trim().max(100).optional(),
};

const INTERNAL_SCHEMA = z.object({
  ...COMMON,
  password: z.string()
    .min(8, 'Min 8 characters')
    .max(200)
    .refine(x => /[A-Z]/.test(x), 'Must contain an uppercase letter')
    .refine(x => /[a-z]/.test(x), 'Must contain a lowercase letter')
    .refine(x => /[0-9]/.test(x), 'Must contain a digit')
    .refine(x => /[^A-Za-z0-9]/.test(x), 'Must contain a special character'),
});

const EXTERNAL_SCHEMA = z.object({
  ...COMMON,
  externalId: z.string().trim().min(1, 'External ID is required').max(200),
});

const EDIT_SCHEMA = z.object({
  ...COMMON,
  isEnabled: z.boolean(),
});

// For a UserAdmin, who keeps what the user signs in with as stored: those fields are shown, not edited, so not validated.
const EDIT_KEEPING_SIGN_IN_SCHEMA = EDIT_SCHEMA.extend({
  userName: z.string(),
  email: z.string().optional(),
});

type CreateInternalForm = z.infer<typeof INTERNAL_SCHEMA>;
type CreateExternalForm = z.infer<typeof EXTERNAL_SCHEMA>;
type EditForm = z.infer<typeof EDIT_SCHEMA>;

interface UserDialogProps {
  open: boolean;
  user?: UserEntry | null;
  onClose: () => void;
}

type Tab = 'internal' | 'external';

export function UserDialog({ open, user, onClose }: UserDialogProps) {
  const isEdit = user != null;
  const [tab, setTab] = useState<Tab>('internal');
  const { isAdmin, isLoading } = useAccess();
  // Only an Admin creates an internal user (whoever sets the password could sign in as them): a UserAdmin adds external
  // users. No form is shown until the caller's permissions are known, so it never switches under the user's typing.
  const shown: Tab = isAdmin ? tab : 'external';

  if (isEdit) {
    return <EditUserForm key={user.id} open={open} user={user} onClose={onClose} />;
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Add user"
      sub={isAdmin
        ? 'Internal users authenticate with a password stored in Beacon. External users are pre-registered and authenticate via JWT/OAuth.'
        : `External users are pre-registered and authenticate via JWT/OAuth. ${INTERNAL_USER_NOTE}`}
      size="md"
      footer={null}
    >
      {isAdmin && (
        <div className="flex gap-1 mb-3">
          <Button
            type="button"
            variant={tab === 'internal' ? 'primary' : 'secondary'}
            onClick={() => setTab('internal')}
          >
            Internal user
          </Button>
          <Button
            type="button"
            variant={tab === 'external' ? 'primary' : 'secondary'}
            onClick={() => setTab('external')}
          >
            External user
          </Button>
        </div>
      )}

      {isLoading
        ? <div className="text-text-muted text-xs">Loading…</div>
        : shown === 'internal'
          ? <CreateInternalForm onClose={onClose} />
          : <CreateExternalForm onClose={onClose} />}
    </Dialog>
  );
}

interface RolePickerProps {
  roles: RoleEntry[];
  /** The roles query's state: while it loads or after it failed, that is shown instead of the roles. */
  status: 'pending' | 'error' | 'success';
  onRetry: () => void;
  selected: ReadonlySet<number>;
  onToggle: (id: number, on: boolean) => void;
  isAdmin: boolean;
}

/**
 * The global roles, then the functional roles (one administrative duty each, without project data). The Admin role
 * and the functional roles are an Admin's to assign or remove: a UserAdmin sees them disabled. A fieldset, not a
 * `Field`: that wraps its content in a label, which would toggle the first role when clicked.
 */
function RolePicker({ roles, status, onRetry, selected, onToggle, isAdmin }: RolePickerProps) {
  const functional = roles.filter(isFunctionalRole);
  const props = { selected, onToggle, isAdmin };

  return (
    <fieldset className="m-0 p-0 border-0 min-w-0">
      <legend className="p-0 mb-1.5 text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">Roles</legend>
      {status === 'pending' ? (
        <div className="text-text-muted text-xs">Loading roles…</div>
      ) : status === 'error' ? (
        <div className="flex items-center gap-2 text-xs text-crit">
          <span>The roles could not be loaded.</span>
          <Button type="button" size="sm" onClick={onRetry}>Retry</Button>
        </div>
      ) : roles.length === 0 ? (
        <div className="text-text-muted text-xs">No roles available.</div>
      ) : (
        <div className="flex flex-col gap-3">
          <RoleGroup title="Global roles" roles={roles.filter(x => !isFunctionalRole(x))} {...props} />
          {functional.length > 0 && (
            <RoleGroup
              title="Functional roles"
              sub="One administrative duty each, without project data: add a Viewer or Editor role for the user to see projects."
              roles={functional}
              {...props}
            />
          )}
          {!isAdmin && (
            <span className="text-xs text-text-muted">
              Only an Admin assigns or removes the Admin role and the functional roles.
            </span>
          )}
        </div>
      )}
    </fieldset>
  );
}

function RoleGroup({
  title,
  sub,
  roles,
  selected,
  onToggle,
  isAdmin,
}: Pick<RolePickerProps, 'selected' | 'onToggle' | 'isAdmin'> & { title: string; sub?: string; roles: RoleEntry[] }) {
  return (
    <fieldset className="m-0 p-0 border-0 min-w-0">
      <legend className="p-0 mb-1 text-2xs font-semibold uppercase tracking-eyebrow text-text-subtle">{title}</legend>
      {sub && <div className="mb-1.5 text-xs text-text-muted">{sub}</div>}
      <div className="grid gap-1.5">
        {roles.map(r => {
          const assignable = mayAssignRole(r, isAdmin);
          return (
            <label key={r.id} className={cn('flex items-center gap-2', !assignable && 'text-text-muted')}>
              <input
                type="checkbox"
                checked={selected.has(r.id)}
                disabled={!assignable}
                onChange={e => onToggle(r.id, e.currentTarget.checked)}
              />
              <span><strong>{r.name}</strong>{r.description ? ` — ${r.description}` : ''}</span>
            </label>
          );
        })}
      </div>
    </fieldset>
  );
}

function toggled(prev: ReadonlySet<number>, id: number, on: boolean): Set<number> {
  const next = new Set(prev);
  if (on) next.add(id); else next.delete(id);
  return next;
}

function CreateInternalForm({ onClose }: { onClose: () => void }) {
  const create = useCreateInternalUser();
  const rolesQuery = useRolesQuery();
  const { isAdmin } = useAccess();
  const [selectedRoles, setSelectedRoles] = useState<Set<number>>(new Set());

  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<CreateInternalForm>({
    resolver: zodResolver(INTERNAL_SCHEMA),
    defaultValues: { userName: '', email: '', displayName: '', password: '' },
  });

  const toggleRole = (id: number, on: boolean) => setSelectedRoles(prev => toggled(prev, id, on));

  const onSubmit = handleSubmit(async values => {
    try {
      await create.mutateAsync({
        userName: values.userName,
        email: values.email?.trim() ? values.email.trim() : null,
        displayName: values.displayName?.trim() ? values.displayName.trim() : null,
        password: values.password,
        roleIds: Array.from(selectedRoles),
      });
      toast.success(`Created user '${values.userName}'`);
      onClose();
    } catch (err) {
      if (err instanceof ApiError && err.status === 400) {
        const title = extractProblemTitle(err.body);
        if (title && /password/i.test(title)) {
          setError('password', { type: 'server', message: title }, { shouldFocus: true });
          return;
        }
      }
      // createSimpleMutation already surfaced the error toast.
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <FieldText label="Username" required autoComplete="username" register={register('userName')} error={errors.userName?.message} />
      <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} placeholder="optional" />
      <FieldText label="Display name" register={register('displayName')} placeholder="optional" />
      <FieldText
        label="Password"
        required
        type="password"
        autoComplete="new-password"
        register={register('password')}
        error={errors.password?.message}
        help="At least 8 characters, with uppercase, lowercase, digit, and special character."
      />

      <RolePicker
        roles={rolesQuery.data?.entries ?? []}
        status={rolesQuery.status}
        onRetry={() => rolesQuery.refetch()}
        selected={selectedRoles}
        onToggle={toggleRole}
        isAdmin={isAdmin}
      />

      <FormFooter onClose={onClose} submitting={isSubmitting} submitLabel="Create user" />
    </form>
  );
}

function CreateExternalForm({ onClose }: { onClose: () => void }) {
  const create = useCreateExternalUser();
  const rolesQuery = useRolesQuery();
  const { isAdmin } = useAccess();
  const [selectedRoles, setSelectedRoles] = useState<Set<number>>(new Set());

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<CreateExternalForm>({
    resolver: zodResolver(EXTERNAL_SCHEMA),
    defaultValues: { externalId: '', userName: '', email: '', displayName: '' },
  });

  const toggleRole = (id: number, on: boolean) => setSelectedRoles(prev => toggled(prev, id, on));

  const onSubmit = handleSubmit(async values => {
    try {
      await create.mutateAsync({
        externalId: values.externalId,
        userName: values.userName,
        email: values.email?.trim() ? values.email.trim() : null,
        displayName: values.displayName?.trim() ? values.displayName.trim() : null,
        roleIds: Array.from(selectedRoles),
      });
      toast.success(`Created user '${values.userName}'`);
      onClose();
    } catch {
      // createSimpleMutation already surfaced the error toast.
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <FieldText
        label="External ID"
        required
        register={register('externalId')}
        error={errors.externalId?.message}
        help="Must match the 'sub' claim from JWT/OAuth."
      />
      <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
      <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} placeholder="optional" />
      <FieldText label="Display name" register={register('displayName')} placeholder="optional" />

      <RolePicker
        roles={rolesQuery.data?.entries ?? []}
        status={rolesQuery.status}
        onRetry={() => rolesQuery.refetch()}
        selected={selectedRoles}
        onToggle={toggleRole}
        isAdmin={isAdmin}
      />

      <FormFooter onClose={onClose} submitting={isSubmitting} submitLabel="Create user" />
    </form>
  );
}

function EditUserForm({ open, user, onClose }: { open: boolean; user: UserEntry; onClose: () => void }) {
  const update = useUpdateUser();
  const setRoles = useSetUserRoles();
  const signOut = useSignUserOutEverywhere();
  const rolesQuery = useRolesQuery();
  const roles = rolesQuery.data?.entries ?? [];
  const { isAdmin } = useAccess();
  const { data: me } = useAuth();
  // A UserAdmin changes neither themselves nor an Admin, a super admin or a functional role holder: shown read-only.
  const blocked = changeBlockedReason(user, { isAdmin, userName: me?.userName }, roles);
  // What a user signs in with, the user name and the e-mail address, is an Admin's to change: a UserAdmin sees it read-only.
  const keepsSignIn = !isAdmin;
  const initialRoleIds = useMemo(() => user.roles.map(x => x.id), [user]);
  const [selectedRoles, setSelectedRoles] = useState<Set<number>>(() => new Set(initialRoleIds));
  const [rolesError, setRolesError] = useState<{ status: number | undefined; message: string } | null>(null);
  const rolesChanged =
    selectedRoles.size !== initialRoleIds.length || initialRoleIds.some(x => !selectedRoles.has(x));
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting, isDirty },
  } = useForm<EditForm>({
    resolver: zodResolver(keepsSignIn ? EDIT_KEEPING_SIGN_IN_SCHEMA : EDIT_SCHEMA),
    defaultValues: {
      userName: user.userName,
      email: user.email ?? '',
      displayName: user.displayName ?? '',
      isEnabled: user.isEnabled,
    },
  });

  useEffect(() => {
    if (open) {
      reset({
        userName: user.userName,
        email: user.email ?? '',
        displayName: user.displayName ?? '',
        isEnabled: user.isEnabled,
      });
      setSelectedRoles(new Set(user.roles.map(x => x.id)));
      setRolesError(null);
    }
  }, [open, user, reset]);

  const onSubmit = handleSubmit(async values => {
    if (blocked) return;
    setRolesError(null);
    if (isDirty || !rolesChanged) {
      try {
        await update.mutateAsync({
          id: user.id,
          payload: {
            userName: keepsSignIn ? user.userName : values.userName,
            email: keepsSignIn ? (user.email ?? null) : (values.email?.trim() ? values.email.trim() : null),
            displayName: values.displayName?.trim() ? values.displayName.trim() : null,
            isEnabled: values.isEnabled,
          },
        });
      } catch {
        // createSimpleMutation already surfaced the error toast.
        return;
      }
    }
    if (rolesChanged) {
      try {
        await setRoles.mutateAsync({ id: user.id, roleIds: Array.from(selectedRoles) });
      } catch (err) {
        // 400: separation of duties or an unknown role; 403: a change the caller may not make. Shown by the roles.
        setRolesError({ status: httpErrorInfo(err)?.status, message: describeError(err, 'Saving the roles failed') });
        return;
      }
    }
    toast.success(`Updated user '${values.userName}'`);
    onClose();
  });

  // Ends every session the user holds (they sign in again); allowed by the same rules as any other change to the user.
  const onSignOutEverywhere = async () => {
    try {
      await signOut.mutateAsync(user.id);
      toast.success(`Signed out every session of '${user.userName}'`);
    } catch {
      // createSimpleMutation already surfaced the error toast.
    }
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Edit user"
      sub={user.isInternalUser ? 'Internal user' : `External — ${user.userName}`}
      size="md"
      footer={
        <>
          <Button
            type="button"
            className="mr-auto"
            onClick={onSignOutEverywhere}
            disabled={isSubmitting || signOut.isPending || blocked != null}
            title="Ends every session of this user in every browser; they sign in again. Their API keys keep working."
          >
            {signOut.isPending ? 'Signing out…' : 'Sign out everywhere'}
          </Button>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>Cancel</Button>
          <Button type="submit" form="edit-user-form" variant="primary" disabled={isSubmitting || blocked != null}>
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </Button>
        </>
      }
    >
      <form id="edit-user-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
        {blocked && <Banner tone="info" icon={<Lock />} title="Read only" sub={blocked} />}
        <fieldset disabled={blocked != null} className="m-0 p-0 border-0 min-w-0 flex flex-col gap-3">
          {keepsSignIn ? (
            <>
              <ShownText label="Username" value={user.userName} />
              <ShownText label="Email" value={user.email ?? ''} />
              <span className="text-xs text-text-muted">{SIGN_IN_IDENTITY_NOTE}</span>
            </>
          ) : (
            <>
              <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
              <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} />
            </>
          )}
          <FieldText label="Display name" register={register('displayName')} />
          <Field label="Status" hint={user.isSuperAdmin ? 'Super admin accounts cannot be disabled.' : undefined}>
            <label className="flex items-center gap-2">
              <input type="checkbox" {...register('isEnabled')} disabled={user.isSuperAdmin} />
              <span>Account enabled</span>
            </label>
          </Field>
          <RolePicker
            roles={roles}
            status={rolesQuery.status}
            onRetry={() => rolesQuery.refetch()}
            selected={selectedRoles}
            onToggle={(id, on) => setSelectedRoles(prev => toggled(prev, id, on))}
            isAdmin={isAdmin}
          />
        </fieldset>
        {rolesError && (
          <Banner
            tone="crit"
            icon={<AlertTriangle />}
            title={rolesError.status === 403 ? 'You may not make this role change' : 'The roles were not saved'}
            sub={rolesError.message}
          />
        )}
      </form>
      <OwnedResources userId={user.id} />
    </Dialog>
  );
}

interface FieldTextProps {
  label: string;
  register: ReturnType<ReturnType<typeof useForm>['register']>;
  required?: boolean;
  type?: string;
  placeholder?: string;
  help?: string;
  error?: string;
  autoComplete?: string;
}

function FieldText({ label, register, required, type = 'text', placeholder, help, error, autoComplete = 'off' }: FieldTextProps) {
  return (
    <Field
      label={required ? <>{label} <span className="text-crit">*</span></> : label}
      hint={help}
    >
      <Input
        type={type}
        autoComplete={autoComplete}
        placeholder={placeholder}
        aria-invalid={!!error}
        {...register}
      />
      {error && <span className="text-xs text-crit">{error}</span>}
    </Field>
  );
}

/** A value shown and not edited (not part of the form). */
function ShownText({ label, value }: { label: string; value: string }) {
  return (
    <Field label={label}>
      <Input type="text" value={value} disabled readOnly />
    </Field>
  );
}

function extractProblemTitle(body: string): string | null {
  if (!body) return null;
  try {
    const parsed = JSON.parse(body) as { title?: string; detail?: string };
    return parsed.title ?? parsed.detail ?? null;
  } catch {
    return body;
  }
}

function FormFooter({ onClose, submitting, submitLabel }: { onClose: () => void; submitting: boolean; submitLabel: string }) {
  return (
    <div className="flex justify-end gap-2 mt-4">
      <Button type="button" onClick={onClose} disabled={submitting}>Cancel</Button>
      <Button type="submit" variant="primary" disabled={submitting}>
        {submitting ? 'Saving…' : submitLabel}
      </Button>
    </div>
  );
}
