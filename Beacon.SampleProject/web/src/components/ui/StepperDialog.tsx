import { useState, useCallback, type ReactNode } from 'react';
import type { FieldValues, Path, UseFormReturn } from 'react-hook-form';
import { Dialog, type DialogSize } from './Dialog';
import { Stepper, type StepperStep } from './Stepper';

export interface StepperDialogStep<TForm extends FieldValues> extends StepperStep {
  /**
   * Form fields to validate before advancing past this step.
   * Use the empty array (default) for steps that need no validation.
   */
  fields?: Path<TForm>[];
  /** Renders the step body. */
  render: (ctx: { stepIndex: number; isActive: boolean }) => ReactNode;
}

interface StepperDialogProps<TForm extends FieldValues> {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  sub?: ReactNode;
  size?: DialogSize;
  steps: StepperDialogStep<TForm>[];
  /** RHF form instance — used for per-step `trigger()` validation. */
  form: UseFormReturn<TForm>;
  /** Submit handler — called only when the final step's validation passes. */
  onFinish: () => void | Promise<void>;
  /** Disable submit (e.g. while a mutation is pending). */
  busy?: boolean;
  /** Label for the final-step submit button. Default: "Submit". */
  finishLabel?: string;
  /** Optional id assigned to the dialog content (useful for tests). */
  formId?: string;
  /** Reset to step 0 when the dialog opens. Default true. */
  resetOnOpen?: boolean;
}

/**
 * Multi-step dialog wrapper. Handles step state, per-step RHF validation,
 * and back/next/submit buttons. The caller owns the form instance and
 * `onFinish` (which performs the actual mutation).
 */
export function StepperDialog<TForm extends FieldValues>({
  open,
  onClose,
  title,
  sub,
  size = 'md',
  steps,
  form,
  onFinish,
  busy = false,
  finishLabel = 'Submit',
  formId,
  resetOnOpen: _resetOnOpen = true,
}: StepperDialogProps<TForm>) {
  const [current, setCurrent] = useState(0);

  // Reset to first step every time the dialog reopens.
  // The caller is expected to reset() form values via its own effect.
  const resetIfNeeded = useCallback(() => {
    if (open) {
      setCurrent(0);
    }
  }, [open]);

  // Run on mount + when `open` flips to true.
  // (No effect needed — we recompute on render via a sentinel pattern.)
  // Use a small sentinel state so we only reset on the open transition.
  const [openSentinel, setOpenSentinel] = useState(false);
  if (open && !openSentinel) {
    setOpenSentinel(true);
    resetIfNeeded();
  } else if (!open && openSentinel) {
    setOpenSentinel(false);
  }

  const isFirst = current === 0;
  const isLast = current === steps.length - 1;
  const activeStep = steps[current];

  const goNext = async () => {
    const fields = activeStep?.fields ?? [];
    if (fields.length > 0) {
      const ok = await form.trigger(fields);
      if (!ok) {
        return;
      }
    }
    if (isLast) {
      await onFinish();
      return;
    }
    setCurrent(idx => Math.min(idx + 1, steps.length - 1));
  };

  const goBack = () => {
    setCurrent(idx => Math.max(idx - 1, 0));
  };

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={title}
      sub={sub}
      size={size}
      footer={
        <>
          <button
            type="button"
            className="btn"
            onClick={onClose}
            disabled={busy}
          >
            Cancel
          </button>
          <div style={{ flex: 1 }} />
          <button
            type="button"
            className="btn"
            onClick={goBack}
            disabled={isFirst || busy}
          >
            Back
          </button>
          <button
            type="button"
            className="btn btn--primary"
            onClick={goNext}
            disabled={busy}
            data-testid="stepper-next"
          >
            {isLast ? (busy ? 'Saving…' : finishLabel) : 'Next'}
          </button>
        </>
      }
    >
      <div id={formId}>
        <Stepper steps={steps} current={current} />
        <div className="stepper__panel" data-step-index={current}>
          {steps.map((step, index) => (
            <div
              key={step.id}
              hidden={index !== current}
              role="tabpanel"
              aria-labelledby={`step-${step.id}`}
            >
              {step.render({ stepIndex: index, isActive: index === current })}
            </div>
          ))}
        </div>
      </div>
    </Dialog>
  );
}
