import { Pill } from '@/components/beacon';

/** What a disabled data source means for whoever looks at one. */
export const DISABLED_HINT =
  'A DataSourceAdmin disabled this data source: Beacon makes no connection with it, so every query, preview, subscription run, data-quality check, MCP call and AI path that reads it fails until it is enabled again.';

/** What "transport not verified" means, and how it goes away. */
export const TRANSPORT_NOT_VERIFIED_HINT =
  'This connection does not make the driver verify the server over TLS. It keeps working, but replace the connection with one that verifies the server certificate.';

/**
 * The state badges shown next to the read-only badge: "Disabled" for a disabled source, and "Transport not verified" when
 * its connection does not authenticate the server over TLS. Nothing when it is enabled and its transport is verified or
 * cannot be read.
 */
export function DataSourceStateBadges({
  disabled,
  transportVerified,
}: {
  disabled?: boolean;
  transportVerified?: boolean | null;
}) {
  return (
    <>
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
