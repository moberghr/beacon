import { BeaconApiClient } from './generated/beacon-api';
import { beaconFetch } from '@/lib/csrf';

let cachedClient: BeaconApiClient | undefined;

export function beaconApi(): BeaconApiClient {
  if (cachedClient === undefined) {
    cachedClient = new BeaconApiClient(window.location.origin, { fetch: beaconFetch });
  }
  return cachedClient;
}

export type { BeaconApiClient } from './generated/beacon-api';
