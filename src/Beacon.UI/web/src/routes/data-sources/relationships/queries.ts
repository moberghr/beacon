import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

/**
 * Result/command shapes are hand-written rather than imported from
 * `@/api/generated/beacon-api.ts`: the NSwag config marks every property optional and emits an
 * index signature, so the generated types are strictly looser than these (see tasks/lessons.md,
 * 2026-06-02).
 *
 * Calls go through `fetchJson` rather than the generated `beaconApi()` client — the same
 * `beaconFetch` pipeline (CSRF priming, antiforgery retry, credentials), typed directly by the
 * interfaces below. `npm run codegen` reads the OpenAPI document from a running host, so the
 * generated client cannot gain these methods without one.
 */

const base = (dataSourceId: number) => `/beacon/api/data-sources/${dataSourceId}`;

export const SchemaRelationshipOrigin = {
  ForeignKey: 0,
  Inferred: 1,
  Manual: 2,
} as const;
export type SchemaRelationshipOriginValue =
  (typeof SchemaRelationshipOrigin)[keyof typeof SchemaRelationshipOrigin];

export const SchemaRelationshipCardinality = {
  Unknown: 0,
  OneToOne: 1,
  OneToMany: 2,
  ManyToMany: 3,
} as const;
export type SchemaRelationshipCardinalityValue =
  (typeof SchemaRelationshipCardinality)[keyof typeof SchemaRelationshipCardinality];

export const ORIGIN_LABEL: Record<SchemaRelationshipOriginValue, string> = {
  [SchemaRelationshipOrigin.ForeignKey]: 'Foreign key',
  [SchemaRelationshipOrigin.Inferred]: 'Inferred',
  [SchemaRelationshipOrigin.Manual]: 'Manual',
};

export const CARDINALITY_LABEL: Record<SchemaRelationshipCardinalityValue, string> = {
  [SchemaRelationshipCardinality.Unknown]: 'Unknown',
  [SchemaRelationshipCardinality.OneToOne]: 'One to one',
  [SchemaRelationshipCardinality.OneToMany]: 'One to many',
  [SchemaRelationshipCardinality.ManyToMany]: 'Many to many',
};

export interface SchemaRelationshipEntry {
  id: number;
  sourceSchema: string;
  sourceTable: string;
  sourceColumn: string;
  targetSchema: string;
  targetTable: string;
  targetColumn: string;
  label: string;
  origin: SchemaRelationshipOriginValue;
  cardinality: SchemaRelationshipCardinalityValue;
  confidence: number;
  isVerified: boolean;
  verifiedTime: string | null;
}

interface GetSchemaRelationshipsResult {
  relationships: SchemaRelationshipEntry[];
}

export interface ProposedRelationship {
  sourceSchema: string;
  sourceTable: string;
  sourceColumn: string;
  targetSchema: string;
  targetTable: string;
  targetColumn: string;
  label: string;
  origin: SchemaRelationshipOriginValue;
  cardinality: SchemaRelationshipCardinalityValue;
  confidence: number;
}

interface PreviewDiscoveryResult {
  proposals: ProposedRelationship[];
}

export interface SchemaHealth {
  tableCount: number;
  relationshipCount: number;
  verifiedRelationshipCount: number;
  unverifiedRelationshipCount: number;
  componentCount: number;
  largestComponentSize: number;
  isolatedTables: string[];
  junctionTables: string[];
}

export interface CreateRelationshipPayload {
  sourceSchema: string;
  sourceTable: string;
  sourceColumn: string;
  targetSchema: string;
  targetTable: string;
  targetColumn: string;
  label: string | null;
  cardinality: SchemaRelationshipCardinalityValue;
}

const relationshipsKey = (dataSourceId: number) =>
  ['schema-relationships', dataSourceId] as const;
const healthKey = (dataSourceId: number) => ['schema-health', dataSourceId] as const;

export function useSchemaRelationshipsQuery(dataSourceId: number) {
  return useQuery({
    queryKey: relationshipsKey(dataSourceId),
    queryFn: () =>
      fetchJson<GetSchemaRelationshipsResult>(`${base(dataSourceId)}/relationships`),
    enabled: dataSourceId > 0,
  });
}

export function useSchemaHealthQuery(dataSourceId: number) {
  return useQuery({
    queryKey: healthKey(dataSourceId),
    queryFn: () => fetchJson<SchemaHealth>(`${base(dataSourceId)}/schema-health`),
    enabled: dataSourceId > 0,
  });
}

export function useDiscoveryPreview(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<void, PreviewDiscoveryResult>({
      qc,
      mutationFn: () =>
        fetchJson<PreviewDiscoveryResult>(`${base(dataSourceId)}/relationships/discover-preview`, {
          method: 'POST',
        }),
      errorFallback: 'Relationship discovery failed',
    }),
  );
}

export function useVerifyRelationship(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; isVerified: boolean }, void>({
      qc,
      mutationFn: ({ id, isVerified }) =>
        fetchJson<void>(`${base(dataSourceId)}/relationships/${id}/verify`, {
          method: 'POST',
          body: JSON.stringify({ isVerified }),
        }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: (vars) =>
        vars.isVerified ? 'Relationship verified' : 'Verification removed',
      errorFallback: 'Verification failed',
    }),
  );
}

export function useCreateRelationship(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateRelationshipPayload, void>({
      qc,
      mutationFn: (values) =>
        fetchJson<void>(`${base(dataSourceId)}/relationships`, {
          method: 'POST',
          body: JSON.stringify(values),
        }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: 'Relationship added',
      errorFallback: 'Add relationship failed',
    }),
  );
}

export function useDeleteRelationship(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`${base(dataSourceId)}/relationships/${id}`, { method: 'DELETE' }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: 'Relationship removed',
      errorFallback: 'Delete relationship failed',
    }),
  );
}
