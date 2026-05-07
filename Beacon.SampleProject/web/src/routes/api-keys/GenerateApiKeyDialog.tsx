import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Icon } from '@/components/Icon';
import { ApiError } from '@/lib/api';
import { useCreateApiKey } from './queries';

const SCOPES = ['Read', 'Execute', 'Admin'] as const;
type Scope = (typeof SCOPES)[number];

const SCHEMA = z.object({
  name: z.string().trim().min(1, 'Name is required').max(100),
  scopeRead: z.boolean().optional(),
  scopeExecute: z.boolean().optional(),
  scopeAdmin: z.boolean().optional(),
  expiresAt: z.string().optional(),
}).refine(
  v => Boolean(v.scopeRead || v.scopeExecute || v.scopeAdmin),
  { message: 'Pick at least one scope', path: ['scopeRead'] },
);

type FormValues = z.infer<typeof SCHEMA>;

interface GenerateApiKeyDialogProps {
  open: boolean;
  onClose: () => void;
}

export function GenerateApiKeyDialog({ open, onClose }: GenerateApiKeyDialogProps) {
  const create = useCreateApiKey();

  // The plaintext key lives in component state ONLY for the lifetime of this
  // dialog (CLAUDE.md §1.3 — never persist or log it).
  const [plainKey, setPlainKey] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: { name: '', scopeRead: true, scopeExecute: false, scopeAdmin: false, expiresAt: '' },
  });

  useEffect(() => {
    if (open) {
      setPlainKey(null);
      setCopied(false);
      reset({ name: '', scopeRead: true, scopeExecute: false, scopeAdmin: false, expiresAt: '' });
    }
  }, [open, reset]);

  const handleClose = () => {
    setPlainKey(null);
    setCopied(false);
    onClose();
  };

  const onSubmit = handleSubmit(async values => {
    const scopes: Scope[] = [];
    if (values.scopeRead) scopes.push('Read');
    if (values.scopeExecute) scopes.push('Execute');
    if (values.scopeAdmin) scopes.push('Admin');

    const expiresAt = values.expiresAt?.trim() ? new Date(values.expiresAt) : null;
    if (expiresAt && Number.isNaN(expiresAt.getTime())) {
      toast.error('Invalid expiration date');
      return;
    }

    try {
      const result = await create.mutateAsync({
        name: values.name,
        scopes,
        allowedProjectIds: null,
        expiresAt,
      });
      setPlainKey(result.plainTextKey);
      toast.success('API key generated');
    } catch (err) {
      const message = err instanceof ApiError
        ? err.body || `Generate failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(message);
    }
  });

  const copyKey = async () => {
    if (plainKey == null) return;
    try {
      await navigator.clipboard.writeText(plainKey);
      setCopied(true);
    } catch {
      toast.error('Could not copy automatically — select and copy manually.');
    }
  };

  return (
    <Dialog
      open={open}
      onClose={handleClose}
      title={plainKey ? 'API key generated' : 'Generate API key'}
      sub={plainKey
        ? 'Copy this key now. It will not be shown again.'
        : 'Scoped key for CI pipelines, monitoring bots, and MCP clients.'}
      size="md"
      closeOnBackdrop={!plainKey}
      footer={
        plainKey ? (
          <button type="button" className="btn btn--primary" onClick={handleClose}>
            Done — I've copied the key
          </button>
        ) : (
          <>
            <button type="button" className="btn" onClick={handleClose} disabled={isSubmitting}>
              Cancel
            </button>
            <button type="submit" form="apikey-form" className="btn btn--primary" disabled={isSubmitting}>
              {isSubmitting ? 'Generating…' : 'Generate key'}
            </button>
          </>
        )
      }
    >
      {plainKey ? (
        <div style={{ display: 'grid', gap: 12 }}>
          <div className="empty-state" style={{ borderColor: 'var(--crit)', background: 'var(--crit-bg)' }}>
            <div className="empty-state__icon" style={{ color: 'var(--crit)' }}>
              <Icon.Alert size={20} />
            </div>
            <div>
              <div className="empty-state__title">This key will not be shown again.</div>
              <div className="empty-state__sub">Copy it now and store it securely.</div>
            </div>
          </div>
          <div className="api-key-reveal">
            <div className="api-key-reveal__key">{plainKey}</div>
            <button type="button" className="btn btn--primary" onClick={copyKey}>
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
        </div>
      ) : (
        <form id="apikey-form" onSubmit={onSubmit} noValidate>
          <div className="q-field">
            <label className="q-label" htmlFor="apikey-name">
              Key name<span className="q-label__req">*</span>
            </label>
            <input
              id="apikey-name"
              className={`q-input${errors.name ? ' q-input--error' : ''}`}
              type="text"
              placeholder="e.g. CI Pipeline"
              {...register('name')}
            />
            {errors.name && <div className="q-error">{errors.name.message}</div>}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label">Scopes<span className="q-label__req">*</span></label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 0' }}>
              <input type="checkbox" {...register('scopeRead')} />
              <span><strong>Read</strong> — query data, read configs and reports</span>
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 0' }}>
              <input type="checkbox" {...register('scopeExecute')} />
              <span><strong>Execute</strong> — trigger scans and run jobs</span>
            </label>
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 0' }}>
              <input type="checkbox" {...register('scopeAdmin')} />
              <span><strong>Admin</strong> — full access including user management</span>
            </label>
            {errors.scopeRead && <div className="q-error">{errors.scopeRead.message}</div>}
          </div>

          <div className="q-field" style={{ marginTop: 14 }}>
            <label className="q-label" htmlFor="apikey-expires">Expiration date</label>
            <input
              id="apikey-expires"
              className="q-input"
              type="date"
              {...register('expiresAt')}
            />
            <div className="q-help">Leave empty for a key that does not expire.</div>
          </div>
        </form>
      )}
    </Dialog>
  );
}
