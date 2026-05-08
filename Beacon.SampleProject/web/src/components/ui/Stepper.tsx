import type { ReactNode } from 'react';

export interface StepperStep {
  /** Stable id for the step. */
  id: string;
  /** Short title shown in the header. */
  title: string;
  /** Optional sub-label rendered under the title. */
  description?: string;
}

interface StepperProps {
  steps: StepperStep[];
  /** Zero-based index of the active step. */
  current: number;
  /** Optional click handler — receives the requested step index. */
  onStepClick?: (index: number) => void;
  className?: string;
}

/**
 * Visual step bar for multi-step dialogs. Pure presentation — owns no state.
 * Steps before `current` are marked as `--done`, the active step as `--active`.
 */
export function Stepper({ steps, current, onStepClick, className }: StepperProps) {
  if (steps.length === 0) {
    return null;
  }

  return (
    <div
      className={`stepper${className ? ` ${className}` : ''}`}
      role="list"
      aria-label="Progress"
    >
      {steps.map((step, index) => {
        const isActive = index === current;
        const isDone = index < current;
        const clickable = typeof onStepClick === 'function' && index <= current;
        const stateClass = isActive
          ? ' stepper__step--active'
          : isDone
            ? ' stepper__step--done'
            : '';

        const content: ReactNode = (
          <>
            <span className="stepper__step-bullet" aria-hidden>
              {isDone ? '✓' : index + 1}
            </span>
            <span className="stepper__step-text">
              <span className="stepper__step-title">{step.title}</span>
              {step.description && (
                <span className="stepper__step-desc">{step.description}</span>
              )}
            </span>
          </>
        );

        return (
          <div
            key={step.id}
            className="stepper__group"
            role="listitem"
            aria-current={isActive ? 'step' : undefined}
          >
            {clickable ? (
              <button
                type="button"
                className={`stepper__step${stateClass}`}
                onClick={() => onStepClick?.(index)}
              >
                {content}
              </button>
            ) : (
              <div className={`stepper__step${stateClass}`} aria-disabled={!isActive}>
                {content}
              </div>
            )}
            {index < steps.length - 1 && (
              <div
                className={`stepper__divider${isDone ? ' stepper__divider--done' : ''}`}
                aria-hidden
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
