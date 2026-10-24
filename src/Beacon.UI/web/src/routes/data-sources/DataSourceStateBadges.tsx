import { Pill } from '@/components/beacon';
import { AwaitingApprovalPill, DATA_SOURCE_HELD_NO_REQUEST_HINT } from '@/routes/approvals/AwaitingApproval';
import { ChangeApprovalKind } from '@/lib/enums';
import type { DataSourceApproval } from './queries';

/** What a disabled data source means for whoever looks at one. */
export const DISABLED_HINT =
  'A DataSourceAdmin disabled this data source: Beacon makes no connection with it, so every query, preview, subscription run, data-quality check, MCP call and AI path that reads it fails until it is enabled again.';

/** What "transport not verified" means, and how it goes away. */
export const TRANSPORT_NOT_VERIFIED_HINT =
  'This connection does not make the driver verify the server over TLS. It keeps working, but replace the connection with one that verifies the server certificate.';

/**
 * The state badges shown next to the read-only badge: "Awaiting approval" while a change of its connection waits for a
 * second person's approval (linking an Approver to it), "Disabled" for a disabled source, and "Transport not verified" when
 * its connection does not authenticate the server over TLS. Nothing when it is enabled, nothing of it waits and its
 * transport is verified or cannot be read.
 */
export function DataSourceStateBadges({
  disabled,
  transportVerified,
  approval,
  awaitsFirstApproval,
}: {
  disabled?: boolean;
  transportVerified?: boolean | null;
  approval?: DataSourceApproval | null;
  awaitsFirstApproval?: boolean;
}) {
  return (
    <>
      {approval != null && (
        <AwaitingApprovalPill kind={ChangeApprovalKind.DataSourceConnection} requestId={approval.requestId} />
      )}
      {approval == null && awaitsFirstApproval === true && (
        <Pill tone="warn" dot title={DATA_SOURCE_HELD_NO_REQUEST_HINT}>
          Held
        </Pill>
      )}
      {disabled === true && (
        <Pill tone="crit" title={DISABLED_HINT}>
          Disabled
        </Pill>
      )}
      {transportVerified === false && (
        <Pill tone="warn" title={TRANSPORT_NOT_VERIFIED_HINT}>
          Transport not verified
        </Pill>
      )}
    </>
  );
}
