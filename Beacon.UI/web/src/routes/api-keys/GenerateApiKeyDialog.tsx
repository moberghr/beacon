import { useEffect, useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { AlertTriangle } from 'lucide-react';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Banner, Field, Input } from '@/components/beacon';
import { describeError } from '@/lib/api';
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
            toast.error(describeError(err, 'Generate failed'));
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
      closeOnEscape={!plainKey}
      footer={
        plainKey ? (
          <Button type="button" variant="primary" onClick={handleClose}>
            Done — I've copied the key
          </Button>
        ) : (
          <>
            <Button type="button" onClick={handleClose} disabled={isSubmitting}>
              Cancel
            </Button>
            <Button type="submit" form="apikey-form" variant="primary" disabled={isSubmitting}>
              {isSubmitting ? 'Generating…' : 'Generate key'}
            </Button>
          </>
        )
      }
    >
      {plainKey ? (
        <div className="grid gap-3">
          <Banner
            tone="crit"
            icon={<AlertTriangle />}
            title="This key will not be shown again."
            sub="Copy it now and store it securely."
          />
          <div className="flex items-center gap-2 p-3 border border-border rounded-md bg-surface-2">
            <div className="font-mono text-sm break-all flex-1">{plainKey}</div>
            <Button type="button" variant="primary" onClick={copyKey}>
              {copied ? 'Copied' : 'Copy'}
            </Button>
          </div>
        </div>
      ) : (
        <form id="apikey-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3.5">
          <Field label={<>Key name <span className="text-crit">*</span></>}>
            <Input
              id="apikey-name"
              type="text"
              placeholder="e.g. CI Pipeline"
              aria-invalid={!!errors.name}
              {...register('name')}
            />
            {errors.name && <span className="text-xs text-crit">{errors.name.message}</span>}
          </Field>

          <Field label={<>Scopes <span className="text-crit">*</span></>}>
            <div className="flex flex-col">
              <label className="flex items-center gap-2 py-1.5">
                <input type="checkbox" {...register('scopeRead')} />
                <span><strong>Read</strong> — query data, read configs and reports</span>
              </label>
              <label className="flex items-center gap-2 py-1.5">
                <input type="checkbox" {...register('scopeExecute')} />
                <span><strong>Execute</strong> — trigger scans and run jobs</span>
              </label>
              <label className="flex items-center gap-2 py-1.5">
                <input type="checkbox" {...register('scopeAdmin')} />
                <span><strong>Admin</strong> — full access including user management</span>
              </label>
            </div>
            {errors.scopeRead && <span className="text-xs text-crit">{errors.scopeRead.message}</span>}
          </Field>

          <Field label="Expiration date" hint="Leave empty for a key that does not expire.">
            <Input
              id="apikey-expires"
              type="date"
              {...register('expiresAt')}
            />
          </Field>
        </form>
      )}
    </Dialog>
  );
}
