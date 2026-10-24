import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';

/** One of the grant states the server reports. */
export type EmbedGrantStatus = 'active' | 'expired' | 'revoked' | 'superseded';

// Local strict interfaces bridged via unwrap<T>(): the generated DTOs type dates as `Date`, the wire carries strings.
export interface EmbedGrantEntry {
  id: number;
  publicId: string;
  name: string;
  projectId: number;
  projectName: string | null;
  queryId: number;
  queryName: string | null;
  queryVersionId: number;
  versionNumber: number;
  allowedOrigins: string[];
  lockedParameters: string[];
  createdByLabel: string | null;
  createdTime: string;
  expiresTime: string;
  revokedTime: string | null;
  lastTokenIssuedTime: string | null;
  status: EmbedGrantStatus;
}

export interface CreateEmbedGrantPayload {
  projectId: number;
  queryId: number;
  name: string;
  allowedOrigins: string[];
  expiresInDays: number;
  fixedParameters: Record<string, string> | null;
  lockedParameters: string[] | null;
}

export interface CreateEmbedGrantResult {
  id: number;
  publicId: string;
  /** The grant secret, shown this once: only its hash is stored. */
  secret: string;
  queryVersionId: number;
  versionNumber: number;
  expiresTime: string;
  allowedOrigins: string[];
  lockedParameters: string[];
}

const KEYS = ['embed-grants'] as const;

/** The embed grants in the projects the caller may read: server-paged, newest first. */
export function useEmbedGrantsList() {
  return usePagedList<EmbedGrantEntry>({
    queryKey: KEYS,
    path: '/beacon/api/embed-grants',
    defaultSort: { column: 'createdTime', direction: 'desc' },
  });
}

export function useCreateEmbedGrant() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateEmbedGrantPayload, CreateEmbedGrantResult>({
      qc,
      mutationFn: async body => unwrap<CreateEmbedGrantResult>(await beaconApi().createEmbedGrant(body)),
      invalidate: [KEYS],
      errorFallback: 'Create embed grant failed',
      // The create dialog shows the server's reason in a banner.
      errorToast: false,
    }),
  );
}

export function useRevokeEmbedGrant() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: id => beaconApi().revokeEmbedGrant(id),
      invalidate: [KEYS],
      errorFallback: 'Revoke embed grant failed',
    }),
  );
}
