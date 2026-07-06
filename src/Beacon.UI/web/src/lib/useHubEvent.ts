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
 * Lazily-initialized singleton hub connection shared across the React app so
 * multiple subscribers don't open separate WebSockets. Subscribers register in
 * a module-level set; whenever a (re)connection is established every live
 * subscriber is re-attached, so long-lived pages keep receiving realtime
 * events even after SignalR's automatic reconnect gives up and the connection
 * has to be rebuilt from scratch.
 */
let hubPromise: Promise<BeaconHub> | undefined;
let hubFailureToastShown = false;
let reconnectTimer: ReturnType<typeof setTimeout> | undefined;

const RECONNECT_DELAY_MS = 15_000;

interface HubSubscriber {
  attach: (hub: BeaconHub) => () => void;
  unsubscribe?: () => void;
}

const subscribers = new Set<HubSubscriber>();

function ensureConnected(): void {
  if (hubPromise !== undefined) {
    // Already connected (or connecting) — attach any subscribers added since.
    hubPromise.then(attachPendingSubscribers).catch(() => {
      // Connection failures are handled by the initiating ensureConnected call.
    });
    return;
  }

  hubPromise = connectBeaconHub().then(hub => {
    // When automatic reconnect exhausts, the connection is dead for good.
    // Drop the cached promise, mark every subscriber detached, and schedule
    // a fresh connection so already-mounted pages recover.
    hub.onClosed(() => {
      console.warn('[beacon-hub] connection closed permanently — scheduling reconnect');
      hubPromise = undefined;
      for (const subscriber of subscribers) {
        subscriber.unsubscribe = undefined;
      }
      scheduleReconnect();
    });
    return hub;
  });

  hubPromise
    .then(attachPendingSubscribers)
    .catch(err => {
      // Hub is non-essential — log but don't crash the page. Reset so a
      // later subscribe (or the scheduled retry) can attempt a fresh connect.
      hubPromise = undefined;
      console.warn('[beacon-hub] failed to connect', err);
      if (!hubFailureToastShown) {
        hubFailureToastShown = true;
        toast.warning('Realtime updates unavailable', {
          description: 'Lists won’t refresh automatically until the connection recovers. Use the refresh button to pull latest data.',
        });
      }
      scheduleReconnect();
    });
}

function attachPendingSubscribers(hub: BeaconHub): void {
  for (const subscriber of subscribers) {
    if (subscriber.unsubscribe === undefined) {
      subscriber.unsubscribe = subscriber.attach(hub);
    }
  }
}

function scheduleReconnect(): void {
  if (reconnectTimer !== undefined || subscribers.size === 0) {
    return;
  }
  reconnectTimer = setTimeout(() => {
    reconnectTimer = undefined;
    if (subscribers.size > 0) {
      ensureConnected();
    }
  }, RECONNECT_DELAY_MS);
}

/**
 * Register a subscriber that is (re)attached to every hub connection for as
 * long as it stays registered. Returns a removal function — StrictMode-safe
 * because a removed subscriber is skipped when the pending attach resolves.
 */
function addSubscriber(attach: (hub: BeaconHub) => () => void): () => void {
  const subscriber: HubSubscriber = { attach };
  subscribers.add(subscriber);
  ensureConnected();
  return () => {
    subscribers.delete(subscriber);
    subscriber.unsubscribe?.();
    subscriber.unsubscribe = undefined;
  };
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

  useEffect(
    () =>
      addSubscriber(hub => {
        const method = METHOD_BY_EVENT[event];
        // The hub methods all share the shape `(handler) => unsubscribe`.
        const subscribe = hub[method] as unknown as (
          h: (payload: EventMap[E]) => void,
        ) => () => void;
        return subscribe(payload => handlerRef.current(payload));
      }),
    [event],
  );
}

/**
 * Run a callback whenever the shared hub connection reconnects after a
 * transient drop — events fired while disconnected are gone, so callers
 * should reconcile (typically by invalidating their query keys).
 */
export function useHubReconnected(handler: () => void) {
  const handlerRef = useRef(handler);
  handlerRef.current = handler;

  useEffect(() => addSubscriber(hub => hub.onReconnected(() => handlerRef.current())), []);
}
