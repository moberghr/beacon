import { useEffect, useRef } from 'react';
import { toast } from 'sonner';
import {
  connectBeaconHub,
  type BeaconHub,
  type ApprovalUpdatedEvent,
  type JobStatusChangedEvent,
  type NotificationCreatedEvent,
} from './hub';

/**
 * Lazily-initialized singleton hub connection. We share one connection across
 * the React app so multiple subscribers don't open separate WebSockets. The
 * connection is created on first subscribe; we never explicitly stop it (the
 * page unload tears it down).
 */
let hubPromise: Promise<BeaconHub> | undefined;
let hubFailureToastShown = false;

function getHub(): Promise<BeaconHub> {
  if (hubPromise === undefined) {
    hubPromise = connectBeaconHub().catch(err => {
      // Reset so a subsequent subscribe attempt can retry.
      hubPromise = undefined;
      throw err;
    });
  }
  return hubPromise;
}

type EventMap = {
  ApprovalUpdated: ApprovalUpdatedEvent;
  JobStatusChanged: JobStatusChangedEvent;
  NotificationCreated: NotificationCreatedEvent;
};

type HubMethod<E extends keyof EventMap> = E extends 'ApprovalUpdated'
  ? 'onApprovalUpdated'
  : E extends 'JobStatusChanged'
    ? 'onJobStatusChanged'
    : 'onNotificationCreated';

const METHOD_BY_EVENT: { [K in keyof EventMap]: HubMethod<K> } = {
  ApprovalUpdated: 'onApprovalUpdated',
  JobStatusChanged: 'onJobStatusChanged',
  NotificationCreated: 'onNotificationCreated',
};

/**
 * Subscribe to a SignalR event for the lifetime of the calling component.
 * The handler is stored in a ref so callers don't need to memoize it.
 *
 * Example:
 *   useHubEvent('ApprovalUpdated', () => {
 *     queryClient.invalidateQueries({ queryKey: ['approvals'] });
 *   });
 */
export function useHubEvent<E extends keyof EventMap>(
  event: E,
  handler: (payload: EventMap[E]) => void,
) {
  const handlerRef = useRef(handler);
  handlerRef.current = handler;

  useEffect(() => {
    let unsubscribe: (() => void) | undefined;
    let cancelled = false;

    getHub()
      .then(hub => {
        if (cancelled) {
          return;
        }
        const method = METHOD_BY_EVENT[event];
        // The hub methods all share the shape `(handler) => unsubscribe`.
        const subscribe = hub[method] as unknown as (
          h: (payload: EventMap[E]) => void,
        ) => () => void;
        unsubscribe = subscribe(payload => handlerRef.current(payload));
      })
      .catch(err => {
        // Hub is non-essential — log but don't crash the page.
        // eslint-disable-next-line no-console
        console.warn('[beacon-hub] failed to subscribe', event, err);
        if (!hubFailureToastShown) {
          hubFailureToastShown = true;
          toast.warning('Realtime updates unavailable', {
            description: 'Lists won’t refresh automatically until the connection recovers. Use the refresh button to pull latest data.',
          });
        }
      });

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, [event]);
}
