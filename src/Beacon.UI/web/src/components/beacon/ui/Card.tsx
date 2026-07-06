import * as React from 'react';
import { cn } from '@/lib/cn';

export function Card({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('bg-surface border border-border rounded-md shadow-sm overflow-hidden', className)}
      {...props}
    />
  );
}

export function CardHead({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('flex items-center gap-2 px-4 py-2.5 border-b border-border', className)}
      {...props}
    />
  );
}

export function CardTitle({ className, ...props }: React.HTMLAttributes<HTMLHeadingElement>) {
  return <h3 className={cn('m-0 text-sm font-semibold text-text', className)} {...props} />;
}

export function CardSub({ className, ...props }: React.HTMLAttributes<HTMLSpanElement>) {
  return <span className={cn('text-xs text-text-muted', className)} {...props} />;
}

export function CardActions({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('ml-auto flex items-center gap-1.5', className)} {...props} />;
}

export function CardBody({
  className,
  flush,
  ...props
}: React.HTMLAttributes<HTMLDivElement> & { flush?: boolean }) {
  return <div className={cn(flush ? '' : 'p-4', className)} {...props} />;
}
