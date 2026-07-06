import * as React from 'react';
import { cn } from '@/lib/cn';

export function Kbd({ className, ...props }: React.HTMLAttributes<HTMLSpanElement>) {
  return <span className={cn('kbd', className)} {...props} />;
}
