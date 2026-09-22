import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';

const connectBeaconHub = vi.fn();
vi.mock('./hub', () => ({
  connectBeaconHub: () => connectBeaconHub(),
}));

const useAuth = vi.fn();
vi.mock('@/auth/useAuth', () => ({
  useAuth: () => useAuth(),
}));

/** A hub that resolves but never fires, so `connectBeaconHub` calls are the only signal. */
function stubHub() {
  return Promise.resolve({
    onJobStatusChanged: () => () => undefined,
    onNotificationCreated: () => () => undefined,
    onApprovalUpdated: () => () => undefined,
    onReconnected: () => () => undefined,
    onClosed: () => undefined,
    stop: () => Promise.resolve(),
  });
}

/**
 * The module keeps a singleton `hubPromise`, so a connection opened by one test would
 * satisfy the next one's `ensureConnected()` and mask a regression. Re-import per test.
 */
async function freshModule() {
  vi.resetModules();
  return import('./useHubEvent');
}

describe('useHubEvent realtime gate', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    connectBeaconHub.mockImplementation(stubHub);
  });

  it('does not open a hub connection when the server reports realtime disabled', async () => {
    useAuth.mockReturnValue({ data: { realtimeEnabled: false } });
    const { useHubEvent } = await freshModule();

    renderHook(() => useHubEvent('ApprovalUpdated', () => undefined));

    expect(connectBeaconHub).not.toHaveBeenCalled();
  });

  it('connects when the server reports realtime enabled', async () => {
    useAuth.mockReturnValue({ data: { realtimeEnabled: true } });
    const { useHubEvent } = await freshModule();

    renderHook(() => useHubEvent('ApprovalUpdated', () => undefined));

    expect(connectBeaconHub).toHaveBeenCalled();
  });

  it('connects while /auth/me is still loading, so the common case pays no delay', async () => {
    // `undefined` is "not known yet", NOT "disabled" — only an explicit false suppresses.
    useAuth.mockReturnValue({ data: undefined });
    const { useHubEvent } = await freshModule();

    renderHook(() => useHubEvent('ApprovalUpdated', () => undefined));

    expect(connectBeaconHub).toHaveBeenCalled();
  });

  it('gates useHubReconnected on the same flag', async () => {
    useAuth.mockReturnValue({ data: { realtimeEnabled: false } });
    const { useHubReconnected } = await freshModule();

    renderHook(() => useHubReconnected(() => undefined));

    expect(connectBeaconHub).not.toHaveBeenCalled();
  });
});
