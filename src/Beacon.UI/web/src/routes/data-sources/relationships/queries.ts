import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { ApiError, describeError, fetchJson } from '@/lib/api';
import { usePagedList } from '@/lib/usePagedList';
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
  /**
   * Whether the caller may verify or un-verify it: the project admin role in every live project linking the data source,
   * the DataSourceAdmin permission, or an Admin.
   */
  canCurate: boolean;
}

/** Why the verify action is not offered (see `SchemaRelationshipEntry.canCurate`). */
export const VERIFY_CURATE_REASON =
  'Only a project admin of every project linking this data source, a DataSourceAdmin or an Admin verifies relationships.';

/** What `POST …/relationships` answers: a relationship added by someone who may not curate it waits unverified. */
export interface CreateRelationshipResult {
  id: number;
  isVerified: boolean;
}

/**
 * What `PUT …/relationships/{id}` answers: whether the relationship is verified after the change — a verified one that
 * someone who may not curate the source changes waits for a curator's verification again.
 */
export interface UpdateRelationshipResult {
  isVerified: boolean;
}

export interface UpdateRelationshipPayload {
  id: number;
  label: string | null;
  cardinality: SchemaRelationshipCardinalityValue;
}

/** Why deleting a verified relationship is not offered: a verified join is a curator's to remove. */
export const DELETE_VERIFIED_CURATE_REASON =
  'A verified relationship is removed only by a project admin of every project linking this data source, a DataSourceAdmin or an Admin.';

/** What a change says when the relationship waits for a curator's verification afterwards. */
export const PENDING_CURATOR_VERIFICATION = 'pending curator verification';

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

/** A data source's registered relationships: server-paged, by source schema/table/column. */
export function useSchemaRelationshipsList(dataSourceId: number) {
  return usePagedList<SchemaRelationshipEntry>({
    queryKey: relationshipsKey(dataSourceId),
    path: `${base(dataSourceId)}/relationships`,
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
    createSimpleMutation<CreateRelationshipPayload, CreateRelationshipResult>({
      qc,
      mutationFn: (values) =>
        fetchJson<CreateRelationshipResult>(`${base(dataSourceId)}/relationships`, {
          method: 'POST',
          body: JSON.stringify(values),
        }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: (_, result) =>
        result?.isVerified === false
          ? 'Relationship added, pending verification by a curator'
          : 'Relationship added',
      errorFallback: 'Add relationship failed',
    }),
  );
}

export function useUpdateRelationship(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<UpdateRelationshipPayload, UpdateRelationshipResult>({
      qc,
      mutationFn: ({ id, label, cardinality }) =>
        fetchJson<UpdateRelationshipResult>(`${base(dataSourceId)}/relationships/${id}`, {
          method: 'PUT',
          body: JSON.stringify({ label, cardinality }),
        }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: (_, result) =>
        result?.isVerified === false
          ? `Relationship updated, ${PENDING_CURATOR_VERIFICATION}`
          : 'Relationship updated',
      errorFallback: 'Update relationship failed',
    }),
  );
}

/**
 * What the server's 403 on removing a relationship means — it carries no reason of its own: the relationship is verified
 * and the caller may not curate the data source.
 */
export const VERIFIED_DELETE_REFUSED =
  'Removing a verified relationship needs the project admin role in every project that links the data source, or the DataSourceAdmin permission.';

export function useDeleteRelationship(dataSourceId: number) {
  const qc = useQueryClient();
  return useMutation({
    ...createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`${base(dataSourceId)}/relationships/${id}`, { method: 'DELETE' }),
      invalidate: [relationshipsKey(dataSourceId), healthKey(dataSourceId)],
      successMsg: 'Relationship removed',
      errorFallback: 'Delete relationship failed',
      errorToast: false,
    }),
    onError: (err: unknown) => {
      toast.error(
        err instanceof ApiError && err.status === 403
          ? VERIFIED_DELETE_REFUSED
          : describeError(err, 'Delete relationship failed'),
      );
    },
  });
}
