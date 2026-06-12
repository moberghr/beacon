import type { ReactNode } from 'react';
import { Check } from 'lucide-react';
import { cn } from '@/lib/cn';

export interface StepperStep {
  id: string;
  title: string;
  description?: string;
}

interface StepperProps {
  steps: StepperStep[];
  /** Zero-based index of the active step. */
  current: number;
  onStepClick?: (index: number) => void;
  className?: string;
}

/**
 * Horizontal step bar for multi-step dialogs. Pure presentation, owns no state.
 */
export function Stepper({ steps, current, onStepClick, className }: StepperProps) {
  if (steps.length === 0) {
    return null;
  }

  return (
    <div
      className={cn('flex items-center gap-2 w-full', className)}
      role="list"
      aria-label="Progress"
    >
      {steps.map((step, index) => {
        const isActive = index === current;
        const isDone = index < current;
        const clickable = typeof onStepClick === 'function' && index <= current;

        const bulletCls = cn(
          'shrink-0 size-6 grid place-items-center rounded-full text-2xs font-semibold mono border transition',
          isActive && 'bg-brand-500 text-white border-brand-600 shadow-ring',
          isDone && 'bg-brand-100 text-brand-700 border-brand-200',
          !isActive && !isDone && 'bg-surface text-text-subtle border-border-strong',
        );
        const titleCls = cn(
          'text-sm font-medium leading-tight',
          isActive ? 'text-text' : isDone ? 'text-text-muted' : 'text-text-subtle',
        );

        const content: ReactNode = (
          <>
            <span className={bulletCls} aria-hidden>
              {isDone ? <Check size={12} /> : index + 1}
            </span>
            <span className="flex flex-col min-w-0">
              <span id={`step-${step.id}`} className={titleCls}>
                {step.title}
              </span>
              {step.description && (
                <span className="text-xs text-text-subtle truncate">{step.description}</span>
              )}
            </span>
          </>
        );

        const stepCls = cn(
          'flex items-center gap-2 px-2 py-1 rounded-sm transition',
          clickable && 'cursor-pointer hover:bg-surface-2',
        );

        return (
          <div
            key={step.id}
            className="flex items-center gap-2 min-w-0"
            role="listitem"
            aria-current={isActive ? 'step' : undefined}
          >
            {clickable ? (
              <button type="button" className={stepCls} onClick={() => onStepClick?.(index)}>
                {content}
              </button>
            ) : (
              <div className={stepCls}>{content}</div>
            )}
            {index < steps.length - 1 && (
              <div
                className={cn(
                  'h-px flex-1 min-w-[18px]',
                  isDone ? 'bg-brand-300' : 'bg-border',
                )}
                aria-hidden
              />
            )}
          </div>
        );
      })}
    </div>
  );
}
