import { Lock } from 'lucide-react';
import { Pill } from '@/components/beacon';

/** A restricted project: only its members and Admins reach it. */
export function RestrictedPill() {
  return (
    <Pill tone="warn" title="Restricted: only members and Admins can use this project">
      <Lock className="size-3" aria-hidden /> Restricted
    </Pill>
  );
}
