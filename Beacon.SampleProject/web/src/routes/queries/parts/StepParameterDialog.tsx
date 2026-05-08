import { useEffect, useMemo } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Dialog } from '@/components/ui/Dialog';
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

/**
 * Prompts the user for runtime values for `{name}`-style step parameters
 * before a step preview runs. Mirrors the legacy
 * `ExecuteStepParametersDialog.razor` shape — one input per parameter, with
 * a type-appropriate input control. All values are sent to the backend as
 * strings (the executor parses them based on the declared type).
 */
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
      // All fields required — empty string fails. Type coercion happens
      // server-side; the form just guarantees a non-empty value.
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
          <button type="button" className="btn" onClick={onClose} disabled={isSubmitting}>
            Cancel
          </button>
          <button
            type="submit"
            form="step-parameter-form"
            className="btn btn--primary"
            disabled={isSubmitting || parameters.length === 0}
          >
            Run preview
          </button>
        </>
      }
    >
      <form id="step-parameter-form" onSubmit={submit} noValidate>
        {parameters.length === 0 ? (
          <div className="muted">This step has no parameters.</div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {parameters.map(p => {
              const inputType =
                p.type === PARAMETER_TYPE.Number
                  ? 'number'
                  : p.type === PARAMETER_TYPE.DateTime
                    ? 'datetime-local'
                    : 'text';
              return (
                <div key={p.name} className="q-field">
                  <label className="q-label" htmlFor={`param-${p.name}`}>
                    {`{${p.name}}`}
                  </label>
                  {p.description && (
                    <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>
                      {p.description}
                    </div>
                  )}
                  <input
                    id={`param-${p.name}`}
                    className={`q-input${errors[p.name] ? ' q-input--error' : ''}`}
                    type={inputType}
                    {...register(p.name)}
                  />
                  {errors[p.name] && (
                    <div className="q-error">{errors[p.name]?.message as string}</div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </form>
    </Dialog>
  );
}
