import { useEffect, useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Dialog } from '@/components/ui/Dialog';
import { Button, Field, Input } from '@/components/beacon';
import { PARAMETER_TYPE, type ParameterTypeId, type ParameterValueInput } from '../queries';

interface ParameterDescriptor {
  name: string;
  type: ParameterTypeId;
  description?: string | null;
}

interface StepParameterDialogProps {
  open: boolean;
  stepOrder: number | null;
  parameters: ParameterDescriptor[];
  onClose: () => void;
  onSubmit: (values: ParameterValueInput[]) => void;
}

export function StepParameterDialog({
  open,
  stepOrder,
  parameters,
  onClose,
  onSubmit,
}: StepParameterDialogProps) {
  const schema = useMemo(() => {
    const shape: Record<string, z.ZodString> = {};
    for (const p of parameters) {
      shape[p.name] = z.string().trim().min(1, 'Required');
    }
    return z.object(shape);
  }, [parameters]);

  const {
    register,
    handleSubmit,
    reset,
    formState: { errors, isSubmitting },
  } = useForm<Record<string, string>>({
    resolver: zodResolver(schema),
    defaultValues: Object.fromEntries(parameters.map(p => [p.name, ''])),
  });

  useEffect(() => {
    if (open) {
      reset(Object.fromEntries(parameters.map(p => [p.name, ''])));
    }
  }, [open, parameters, reset]);

  const submit = handleSubmit(values => {
    const result: ParameterValueInput[] = parameters.map(p => ({
      name: p.name,
      value: values[p.name] ?? '',
    }));
    onSubmit(result);
  });

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={stepOrder == null ? 'Step parameters' : `Step ${stepOrder} parameters`}
      sub="Provide values for parameters referenced in this step's SQL."
      size="md"
      footer={
        <>
          <Button type="button" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </Button>
          <Button
            type="submit"
            form="step-parameter-form"
            variant="primary"
            disabled={isSubmitting || parameters.length === 0}
          >
            Run preview
          </Button>
        </>
      }
    >
      <form id="step-parameter-form" onSubmit={submit} noValidate>
        {parameters.length === 0 ? (
          <div className="text-text-muted">This step has no parameters.</div>
        ) : (
          <div className="flex flex-col gap-3">
            {parameters.map(p => {
              const inputType =
                p.type === PARAMETER_TYPE.Number
                  ? 'number'
                  : p.type === PARAMETER_TYPE.DateTime
                    ? 'datetime-local'
                    : 'text';
              return (
                <Field
                  key={p.name}
                  label={<span className="mono normal-case tracking-normal">{`{${p.name}}`}</span>}
                  hint={p.description ?? undefined}
                >
                  <Input
                    id={`param-${p.name}`}
                    type={inputType}
                    aria-invalid={!!errors[p.name]}
                    {...register(p.name)}
                  />
                  {errors[p.name] && (
                    <span className="text-xs text-crit">{errors[p.name]?.message as string}</span>
                  )}
                </Field>
              );
            })}
          </div>
        )}
      </form>
    </Dialog>
  );
}
