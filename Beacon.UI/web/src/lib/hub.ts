import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';

export interface JobStatusChangedEvent {
  jobId: string;
  state: string;
  timestamp: string;
}

export interface NotificationCreatedEvent {
  notificationId: number;
  kind: string;
}

export interface ApprovalUpdatedEvent {
  approvalId: number;
  status: string;
}

export interface BeaconHub {
  onJobStatusChanged(handler: (event: JobStatusChangedEvent) => void): () => void;
  onNotificationCreated(handler: (event: NotificationCreatedEvent) => void): () => void;
  onApprovalUpdated(handler: (event: ApprovalUpdatedEvent) => void): () => void;
  /** Fires after SignalR's automatic reconnect succeeds. Returns unsubscribe. */
  onReconnected(handler: () => void): () => void;
  /** Fires once when automatic reconnect gives up and the connection dies. */
  onClosed(handler: () => void): void;
  stop(): Promise<void>;
}

export async function connectBeaconHub(): Promise<BeaconHub> {
  const connection: HubConnection = new HubConnectionBuilder()
    .withUrl('/beacon/api/hub', { withCredentials: true })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  // SignalR's onreconnected has no removal API — fan out through our own set
  // so React subscribers can unsubscribe on unmount.
  const reconnectedHandlers = new Set<() => void>();
  connection.onreconnected(() => {
    for (const handler of reconnectedHandlers) {
      handler();
    }
  });

  await connection.start();

  const subscribe = <T>(eventName: string, handler: (event: T) => void) => {
    connection.on(eventName, handler);
    return () => connection.off(eventName, handler);
  };

  return {
    onJobStatusChanged: handler => subscribe('JobStatusChanged', handler),
    onNotificationCreated: handler => subscribe('NotificationCreated', handler),
    onApprovalUpdated: handler => subscribe('ApprovalUpdated', handler),
    onReconnected: handler => {
      reconnectedHandlers.add(handler);
      return () => reconnectedHandlers.delete(handler);
    },
    onClosed: handler => connection.onclose(() => handler()),
    stop: () => connection.stop(),
  };
}
