import { useState, useCallback, type ReactNode } from 'react';
import type { FieldValues, Path, UseFormReturn } from 'react-hook-form';
import { Dialog, type DialogSize } from './Dialog';
import { Stepper, type StepperStep } from './Stepper';
import { Button } from '@/components/beacon';

export interface StepperDialogStep<TForm extends FieldValues> extends StepperStep {
  /** Form fields to validate before advancing past this step. */
  fields?: Path<TForm>[];
  render: (ctx: { stepIndex: number; isActive: boolean }) => ReactNode;
}

interface StepperDialogProps<TForm extends FieldValues> {
  open: boolean;
  onClose: () => void;
  title: ReactNode;
  sub?: ReactNode;
  size?: DialogSize;
  steps: StepperDialogStep<TForm>[];
  form: UseFormReturn<TForm>;
  onFinish: () => void | Promise<void>;
  busy?: boolean;
  finishLabel?: string;
  formId?: string;
  resetOnOpen?: boolean;
}

/**
 * Multi-step dialog wrapper. Handles step state, per-step RHF validation,
 * and back/next/submit buttons.
 */
export function StepperDialog<TForm extends FieldValues>({
  open,
  onClose,
  title,
  sub,
  size = 'lg',
  steps,
  form,
  onFinish,
  busy = false,
  finishLabel = 'Submit',
  formId,
  resetOnOpen: _resetOnOpen = true,
}: StepperDialogProps<TForm>) {
  const [current, setCurrent] = useState(0);

  const resetIfNeeded = useCallback(() => {
    if (open) {
      setCurrent(0);
    }
  }, [open]);

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
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <div className="flex-1" />
          <Button onClick={goBack} disabled={isFirst || busy}>
            Back
          </Button>
          <Button
            variant="primary"
            onClick={goNext}
            disabled={busy}
            data-testid="stepper-next"
          >
            {isLast ? (busy ? 'Saving…' : finishLabel) : 'Next'}
          </Button>
        </>
      }
    >
      <div id={formId} className="flex flex-col gap-5">
        <Stepper steps={steps} current={current} />
        <div data-step-index={current}>
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
