import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { ApiError } from '@/lib/api';
import {
  useCreateExternalUser,
  useCreateInternalUser,
  useRolesQuery,
  useUpdateUser,
  type UserEntry,
} from './queries';

const COMMON = {
  userName: z.string().trim().min(3, 'Min 3 characters').max(100),
  email: z.string().trim().email('Invalid email').max(200).optional().or(z.literal('')),
  displayName: z.string().trim().max(100).optional(),
};

const INTERNAL_SCHEMA = z.object({
  ...COMMON,
  password: z.string().min(8, 'Min 8 characters').max(200),
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
      <div style={{ display: 'flex', gap: 4, marginBottom: 12 }}>
        <button
          type="button"
          className={`btn${tab === 'internal' ? ' btn--primary' : ''}`}
          onClick={() => setTab('internal')}
        >
          Internal user
        </button>
        <button
          type="button"
          className={`btn${tab === 'external' ? ' btn--primary' : ''}`}
          onClick={() => setTab('external')}
        >
          External user
        </button>
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
    return <div className="muted" style={{ fontSize: 12 }}>No roles available.</div>;
  }
  return (
    <div style={{ display: 'grid', gap: 6 }}>
      {roles.map(r => {
        const props = register(r.id);
        return (
          <label key={r.id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
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
      const message = err instanceof ApiError
        ? err.body || `Create failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate>
      <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
      <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} placeholder="optional" />
      <FieldText label="Display name" register={register('displayName')} placeholder="optional" />
      <FieldText label="Password" required type="password" register={register('password')} error={errors.password?.message} />

      <div className="q-field" style={{ marginTop: 14 }}>
        <label className="q-label">Roles</label>
        <RoleCheckboxes
          roles={rolesQuery.data?.entries ?? []}
          register={id => ({
            name: `role-${id}`,
            value: id,
            onChange: e => toggleRole(id, e.currentTarget.checked),
          })}
        />
      </div>

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
      const message = err instanceof ApiError
        ? err.body || `Create failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  return (
    <form onSubmit={onSubmit} noValidate>
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

      <div className="q-field" style={{ marginTop: 14 }}>
        <label className="q-label">Roles</label>
        <RoleCheckboxes
          roles={rolesQuery.data?.entries ?? []}
          register={id => ({
            name: `role-${id}`,
            value: id,
            onChange: e => toggleRole(id, e.currentTarget.checked),
          })}
        />
      </div>

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
      const message = err instanceof ApiError
        ? err.body || `Update failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
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
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>Cancel</button>
          <button type="submit" form="edit-user-form" className="btn btn--primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : 'Save changes'}
          </button>
        </>
      }
    >
      <form id="edit-user-form" onSubmit={onSubmit} noValidate>
        <FieldText label="Username" required register={register('userName')} error={errors.userName?.message} />
        <FieldText label="Email" type="email" register={register('email')} error={errors.email?.message} />
        <FieldText label="Display name" register={register('displayName')} />
        <div className="q-field" style={{ marginTop: 14 }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input type="checkbox" {...register('isEnabled')} disabled={user.isSuperAdmin} />
            <span>Account enabled</span>
          </label>
          {user.isSuperAdmin && <div className="q-help">Super admin accounts cannot be disabled.</div>}
        </div>
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
    <div className="q-field" style={{ marginTop: 12 }}>
      <label className="q-label">
        {label}{required && <span className="q-label__req">*</span>}
      </label>
      <input
        type={type}
        autoComplete="off"
        className={`q-input${error ? ' q-input--error' : ''}`}
        placeholder={placeholder}
        {...register}
      />
      {help && <div className="q-help">{help}</div>}
      {error && <div className="q-error">{error}</div>}
    </div>
  );
}

function FormFooter({ onClose, submitting, submitLabel }: { onClose: () => void; submitting: boolean; submitLabel: string }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
      <button type="button" className="btn" onClick={onClose} disabled={submitting}>Cancel</button>
      <button type="submit" className="btn btn--primary" disabled={submitting}>
        {submitting ? 'Saving…' : submitLabel}
      </button>
    </div>
  );
}
