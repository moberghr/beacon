import * as React from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from '@/lib/cn';

const button = cva(
  'inline-flex items-center gap-1.5 font-medium select-none transition ' +
    'border rounded-sm whitespace-nowrap ' +
    'disabled:opacity-50 disabled:cursor-not-allowed ' +
    'focus-visible:outline-none focus-visible:shadow-ring',
  {
    variants: {
      variant: {
        secondary: [
          'bg-surface text-text border-border-strong',
          'hover:bg-surface-2 hover:border-text-subtle',
        ].join(' '),
        primary: [
          'bg-brand-600 text-white border-brand-700',
          'hover:bg-brand-700 hover:border-brand-800',
          'shadow-sm',
        ].join(' '),
        ghost: [
          'bg-transparent text-text-muted border-transparent',
          'hover:bg-surface-2 hover:text-text',
        ].join(' '),
        resolve: ['bg-ok text-white border-transparent', 'hover:brightness-95 shadow-sm'].join(' '),
        danger: ['bg-crit text-white border-transparent', 'hover:brightness-95 shadow-sm'].join(' '),
      },
      size: {
        sm: 'text-xs px-2 py-1',
        md: 'text-sm px-2.5 py-1.5',
        lg: 'text-base px-3 py-2',
      },
    },
    defaultVariants: { variant: 'secondary', size: 'md' },
  },
);

export interface ButtonProps
  extends React.ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof button> {
  icon?: React.ReactNode;
}

export const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, icon, children, ...props }, ref) => (
    <button ref={ref} className={cn(button({ variant, size }), className)} {...props}>
      {icon && <span className="shrink-0 [&>svg]:size-3.5">{icon}</span>}
      {children}
    </button>
  ),
);
Button.displayName = 'Button';
