import { useEffect } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { toast } from 'sonner';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input, Textarea } from '@/components/beacon';
import {
  useCreateGlossaryTerm,
  useUpdateGlossaryTerm,
  type GlossaryTermEntry,
} from './queries';

const SCHEMA = z.object({
  term: z.string().trim().min(1, 'Term is required').max(200),
  synonyms: z.string().trim().max(1000).optional(),
  definition: z.string().trim().min(1, 'Definition is required').max(4000),
  targetSchema: z.string().trim().max(200).optional(),
  targetTable: z.string().trim().max(200).optional(),
  targetColumn: z.string().trim().max(200).optional(),
  metricExpression: z.string().trim().max(2000).optional(),
  isActive: z.boolean(),
});

type FormValues = z.infer<typeof SCHEMA>;

interface GlossaryTermDialogProps {
  open: boolean;
  onClose: () => void;
  /** The project a new term belongs to (the page's scope). */
  projectId: number;
  /** The term to edit; null creates one. */
  term?: GlossaryTermEntry | null;
}

export function GlossaryTermDialog({ open, onClose, projectId, term }: GlossaryTermDialogProps) {
  const isEdit = term != null;
  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<FormValues>({
    resolver: zodResolver(SCHEMA),
    defaultValues: {
      term: '', synonyms: '', definition: '', targetSchema: '', targetTable: '', targetColumn: '',
      metricExpression: '', isActive: true,
    },
  });
  const createMutation = useCreateGlossaryTerm();
  const updateMutation = useUpdateGlossaryTerm();

  useEffect(() => {
    if (!open) return;
    reset({
      term: term?.term ?? '',
      synonyms: term?.synonyms ?? '',
      definition: term?.definition ?? '',
      targetSchema: term?.targetSchema ?? '',
      targetTable: term?.targetTable ?? '',
      targetColumn: term?.targetColumn ?? '',
      metricExpression: term?.metricExpression ?? '',
      isActive: term?.isActive ?? true,
    });
  }, [open, term, reset]);

  const onSubmit = handleSubmit(async values => {
    // On update the server leaves a null field untouched, so an emptied optional field is sent as '' to clear it.
    const optional = (value: string | undefined) => {
      const trimmed = value?.trim() ?? '';
      return trimmed !== '' ? trimmed : isEdit ? '' : null;
    };
    const body = {
      dataSourceId: term?.dataSourceId ?? null,
      term: values.term,
      synonyms: optional(values.synonyms),
      definition: values.definition,
      targetSchema: optional(values.targetSchema),
      targetTable: optional(values.targetTable),
      targetColumn: optional(values.targetColumn),
      metricExpression: optional(values.metricExpression),
    };
    try {
      if (isEdit) {
        await updateMutation.mutateAsync({ id: term.id, values: { ...body, isActive: values.isActive } });
        toast.success(`Updated term '${values.term}'`);
      } else {
        await createMutation.mutateAsync({ ...body, projectId });
        toast.success(`Created term '${values.term}'`);
      }
      onClose();
    } catch {
      // The mutation hooks already toast the error.
    }
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={isEdit ? 'Edit glossary term' : 'Add a glossary term'}
      sub="A business definition Beacon uses when it answers questions in this project."
      size="md"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>Cancel</Button>
          <Button type="submit" form="glossary-form" variant="primary" disabled={isSubmitting}>
            {isSubmitting ? 'Saving…' : isEdit ? 'Save changes' : 'Create term'}
          </Button>
        </>
      }
    >
      <form id="glossary-form" onSubmit={onSubmit} noValidate className="flex flex-col gap-3.5">
        <Field label={<>Term <span className="text-crit">*</span></>} htmlFor="glossary-term">
          <Input id="glossary-term" autoComplete="off" aria-invalid={!!errors.term} {...register('term')} />
          {errors.term && <span className="text-xs text-crit">{errors.term.message}</span>}
        </Field>
        <Field label="Synonyms" hint="Comma-separated other names for the term." htmlFor="glossary-synonyms">
          <Input id="glossary-synonyms" placeholder="Optional" {...register('synonyms')} />
        </Field>
        <Field label={<>Definition <span className="text-crit">*</span></>} htmlFor="glossary-definition">
          <Textarea id="glossary-definition" rows={4} aria-invalid={!!errors.definition} {...register('definition')} />
          {errors.definition && <span className="text-xs text-crit">{errors.definition.message}</span>}
        </Field>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Target schema" htmlFor="glossary-schema">
            <Input id="glossary-schema" placeholder="Optional" {...register('targetSchema')} />
          </Field>
          <Field label="Target table" htmlFor="glossary-table">
            <Input id="glossary-table" placeholder="Optional" {...register('targetTable')} />
          </Field>
          <Field label="Target column" htmlFor="glossary-column">
            <Input id="glossary-column" placeholder="Optional" {...register('targetColumn')} />
          </Field>
        </div>
        <Field label="Metric expression" hint="A SQL expression that computes the term, e.g. a measure." htmlFor="glossary-metric">
          <Textarea id="glossary-metric" rows={2} placeholder="Optional" {...register('metricExpression')} />
        </Field>
        {isEdit && (
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" {...register('isActive')} />
            Active (an inactive term is not used for answers)
          </label>
        )}
      </form>
    </Dialog>
  );
}
