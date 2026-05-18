import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input } from '@/components/beacon';
import { ApiError, describeError } from '@/lib/api';
import {
  useCreateExternalUser,
  useCreateInternalUser,
  useRolesQuery,
  useUpdateUser,
  type UserEntry,
} from './queries';

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

  if (isEdit) {
    return <EditUserForm key={user.id} open={open} user={user} onClose={onClose} />;
  }

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Add user"
      sub="Internal users authenticate with a password stored in Beacon. External users are pre-registered and authenticate via JWT/OAuth."
      size="md"
      footer={null}
    >
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

      {tab === 'internal'
        ? <CreateInternalForm onClose={onClose} />
        : <CreateExternalForm onClose={onClose} />}
    </Dialog>
  );
}

function RoleCheckboxes({
  roles,
  register,
}: {
  roles: { id: number; name: string; description: string | null }[];
  register: (id: number) => { name: string; value: number; onChange: (e: React.ChangeEvent<HTMLInputElement>) => void };
}) {
  if (roles.length === 0) {
    return <div className="text-text-muted text-xs">No roles available.</div>;
  }
  return (
    <div className="grid gap-1.5">
      {roles.map(r => {
        const props = register(r.id);
        return (
          <label key={r.id} className="flex items-center gap-2">
            <input type="checkbox" {...props} />
            <span><strong>{r.name}</strong>{r.description ? ` — ${r.description}` : ''}</span>
          </label>
        );
      })}
    </div>
  );
}

function CreateInternalForm({ onClose }: { onClose: () => void }) {
  const create = useCreateInternalUser();
  const rolesQuery = useRolesQuery();
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

  const toggleRole = (id: number, on: boolean) => {
    setSelectedRoles(prev => {
      const next = new Set(prev);
      if (on) next.add(id); else next.delete(id);
      return next;
    });
  };

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
            toast.error(describeError(err, 'Create failed'));
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
      <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} placeholder="optional" />
      <FieldText label="Display name" register={register('displayName')} placeholder="optional" />
      <FieldText
        label="Password"
        required
        type="password"
        register={register('password')}
        error={errors.password?.message}
        help="At least 8 characters, with uppercase, lowercase, digit, and special character."
      />

      <Field label="Roles">
        <RoleCheckboxes
          roles={rolesQuery.data?.entries ?? []}
          register={id => ({
            name: `role-${id}`,
            value: id,
            onChange: e => toggleRole(id, e.currentTarget.checked),
          })}
        />
      </Field>

      <FormFooter onClose={onClose} submitting={isSubmitting} submitLabel="Create user" />
    </form>
  );
}

function CreateExternalForm({ onClose }: { onClose: () => void }) {
  const create = useCreateExternalUser();
  const rolesQuery = useRolesQuery();
  const [selectedRoles, setSelectedRoles] = useState<Set<number>>(new Set());

  const {
    register,
    handleSubmit,
    formState: { errors, isSubmitting },
  } = useForm<CreateExternalForm>({
    resolver: zodResolver(EXTERNAL_SCHEMA),
    defaultValues: { externalId: '', userName: '', email: '', displayName: '' },
  });

  const toggleRole = (id: number, on: boolean) => {
    setSelectedRoles(prev => {
      const next = new Set(prev);
      if (on) next.add(id); else next.delete(id);
      return next;
    });
  };

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
    } catch (err) {
            toast.error(describeError(err, 'Create failed'));
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

      <Field label="Roles">
        <RoleCheckboxes
          roles={rolesQuery.data?.entries ?? []}
          register={id => ({
            name: `role-${id}`,
            value: id,
            onChange: e => toggleRole(id, e.currentTarget.checked),
          })}
        />
      </Field>

      <FormFooter onClose={onClose} submitting={isSubmitting} submitLabel="Create user" />
    </form>
  );
}

function EditUserForm({ open, user, onClose }: { open: boolean; user: UserEntry; onClose: () => void }) {
  const update = useUpdateUser();
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<EditForm>({
    resolver: zodResolver(EDIT_SCHEMA),
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
    }
  }, [open, user, reset]);

  const onSubmit = handleSubmit(async values => {
    try {
      await update.mutateAsync({
        id: user.id,
        payload: {
          userName: values.userName,
          email: values.email?.trim() ? values.email.trim() : null,
          displayName: values.displayName?.trim() ? values.displayName.trim() : null,
          isEnabled: values.isEnabled,
        },
      });
      toast.success(`Updated user '${values.userName}'`);
      onClose();
    } catch (err) {
            toast.error(describeError(err, 'Update failed'));
    }
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title="Edit user"
      sub={user.isInternalUser ? 'Internal user' : `External — ${user.userName}`}
      size="md"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>Cancel</Button>
          <Button type="submit" form="edit-user-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </Button>
        </>
      }
    >
      <form id="edit-user-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
        <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
        <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} />
        <FieldText label="Display name" register={register('displayName')} />
        <Field label="Status" hint={user.isSuperAdmin ? 'Super admin accounts cannot be disabled.' : undefined}>
          <label className="flex items-center gap-2">
            <input type="checkbox" {...register('isEnabled')} disabled={user.isSuperAdmin} />
            <span>Account enabled</span>
          </label>
        </Field>
      </form>
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
}

function FieldText({ label, register, required, type = 'text', placeholder, help, error }: FieldTextProps) {
  return (
    <Field
      label={required ? <>{label} <span className="text-crit">*</span></> : label}
      hint={help}
    >
      <Input
        type={type}
        autoComplete="off"
        placeholder={placeholder}
        aria-invalid={!!error}
        {...register}
      />
      {error && <span className="text-xs text-crit">{error}</span>}
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
