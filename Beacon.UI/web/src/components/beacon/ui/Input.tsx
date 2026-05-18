import * as React from 'react';
import { cn } from '@/lib/cn';

const fieldBase =
  'w-full bg-surface text-text border border-border-strong rounded-sm ' +
  'px-2.5 py-1.5 text-sm placeholder:text-text-subtle ' +
  'focus:border-brand-500 focus:outline-none focus:shadow-ring ' +
  'disabled:bg-surface-2 disabled:cursor-not-allowed';

export const Input = React.forwardRef<HTMLInputElement, React.InputHTMLAttributes<HTMLInputElement>>(
  ({ className, ...props }, ref) => (
    <input ref={ref} className={cn(fieldBase, className)} {...props} />
  ),
);
Input.displayName = 'Input';

export const Textarea = React.forwardRef<
  HTMLTextAreaElement,
  React.TextareaHTMLAttributes<HTMLTextAreaElement>
>(({ className, ...props }, ref) => (
  <textarea
    ref={ref}
    rows={3}
    className={cn(fieldBase, 'leading-relaxed resize-y', className)}
    {...props}
  />
));
Textarea.displayName = 'Textarea';

export const Select = React.forwardRef<
  HTMLSelectElement,
  React.SelectHTMLAttributes<HTMLSelectElement>
>(({ className, children, ...props }, ref) => (
  <select ref={ref} className={cn(fieldBase, 'pr-7 appearance-none', className)} {...props}>
    {children}
  </select>
));
Select.displayName = 'Select';

export function Field({
  label,
  hint,
  children,
  className,
}: {
  label: React.ReactNode;
  hint?: React.ReactNode;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <label className={cn('flex flex-col gap-1.5', className)}>
      <span className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
        {label}
      </span>
      {children}
      {hint && <span className="text-xs text-text-subtle">{hint}</span>}
    </label>
  );
}
