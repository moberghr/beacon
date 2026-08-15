import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { ArrowRight, Check, Plus, Search, Trash2, X } from 'lucide-react';
import {
  Banner,
  Button,
  Card,
  CardActions,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  Field,
  Input,
  PageHeader,
  Pill,
  Select,
} from '@/components/beacon';
import { SchemaHealthPanel } from './SchemaHealthPanel';
import {
  CARDINALITY_LABEL,
  ORIGIN_LABEL,
  SchemaRelationshipCardinality,
  SchemaRelationshipOrigin,
  useCreateRelationship,
  useDeleteRelationship,
  useDiscoveryPreview,
  useSchemaHealthQuery,
  useSchemaRelationshipsQuery,
  useVerifyRelationship,
  type CreateRelationshipPayload,
  type ProposedRelationship,
  type SchemaRelationshipEntry,
  type SchemaRelationshipOriginValue,
} from './queries';

const emptyDraft: CreateRelationshipPayload = {
  sourceSchema: '',
  sourceTable: '',
  sourceColumn: '',
  targetSchema: '',
  targetTable: '',
  targetColumn: '',
  label: null,
  cardinality: SchemaRelationshipCardinality.OneToMany,
};

function originTone(origin: SchemaRelationshipOriginValue) {
  if (origin === SchemaRelationshipOrigin.ForeignKey) {
    return 'ok' as const;
  }
  return origin === SchemaRelationshipOrigin.Manual ? 'info' as const : 'warn' as const;
}

export function RelationshipsPage() {
  const params = useParams();
  const dataSourceId = Number(params.id ?? 0);

  const relationships = useSchemaRelationshipsQuery(dataSourceId);
  const health = useSchemaHealthQuery(dataSourceId);
  const discover = useDiscoveryPreview(dataSourceId);
  const verify = useVerifyRelationship(dataSourceId);
  const remove = useDeleteRelationship(dataSourceId);
  const create = useCreateRelationship(dataSourceId);

  const [draft, setDraft] = useState<CreateRelationshipPayload>(emptyDraft);
  const [isAdding, setIsAdding] = useState(false);
  const [proposals, setProposals] = useState<ProposedRelationship[]>([]);

  const entries = relationships.data?.relationships ?? [];

  const runDiscovery = () => {
    discover.mutate(undefined, {
      onSuccess: (result) => setProposals(result.proposals),
    });
  };

  const acceptProposal = (proposal: ProposedRelationship) => {
    create.mutate(
      {
        sourceSchema: proposal.sourceSchema,
        sourceTable: proposal.sourceTable,
        sourceColumn: proposal.sourceColumn,
        targetSchema: proposal.targetSchema,
        targetTable: proposal.targetTable,
        targetColumn: proposal.targetColumn,
        label: proposal.label,
        cardinality: proposal.cardinality,
      },
      {
        onSuccess: () =>
          setProposals((current) => current.filter((x) => x !== proposal)),
      },
    );
  };

  const submitDraft = () => {
    create.mutate(draft, {
      onSuccess: () => {
        setDraft(emptyDraft);
        setIsAdding(false);
      },
    });
  };

  const isDraftComplete =
    draft.sourceSchema.trim() !== '' &&
    draft.sourceTable.trim() !== '' &&
    draft.sourceColumn.trim() !== '' &&
    draft.targetSchema.trim() !== '' &&
    draft.targetTable.trim() !== '' &&
    draft.targetColumn.trim() !== '';

  return (
    <div className="space-y-6">
      <PageHeader
        variant="nodes"
        eyebrow="Data"
        prefix="Schema"
        emphasis="relationships"
        sub="Join paths used to ground generated SQL for this data source"
        actions={
          <div className="flex gap-2">
            <Button
              variant="ghost"
              onClick={runDiscovery}
              disabled={discover.isPending || dataSourceId <= 0}
            >
              <Search className="size-4" aria-hidden />
              {discover.isPending ? 'Discovering…' : 'Discover'}
            </Button>
            <Button onClick={() => setIsAdding((v) => !v)} disabled={dataSourceId <= 0}>
              <Plus className="size-4" aria-hidden />
              Add relationship
            </Button>
          </div>
        }
      />

      <SchemaHealthPanel health={health.data} isLoading={health.isLoading} />

      {isAdding && (
        <Card>
          <CardHead>
            <div>
              <CardTitle>Add relationship</CardTitle>
              <CardSub>Declared relationships are treated as verified</CardSub>
            </div>
          </CardHead>
          <CardBody className="grid gap-3 sm:grid-cols-3">
            <Field label="Source schema">
              <Input
                value={draft.sourceSchema}
                onChange={(e) => setDraft({ ...draft, sourceSchema: e.target.value })}
              />
            </Field>
            <Field label="Source table">
              <Input
                value={draft.sourceTable}
                onChange={(e) => setDraft({ ...draft, sourceTable: e.target.value })}
              />
            </Field>
            <Field label="Source column">
              <Input
                value={draft.sourceColumn}
                onChange={(e) => setDraft({ ...draft, sourceColumn: e.target.value })}
              />
            </Field>
            <Field label="Target schema">
              <Input
                value={draft.targetSchema}
                onChange={(e) => setDraft({ ...draft, targetSchema: e.target.value })}
              />
            </Field>
            <Field label="Target table">
              <Input
                value={draft.targetTable}
                onChange={(e) => setDraft({ ...draft, targetTable: e.target.value })}
              />
            </Field>
            <Field label="Target column">
              <Input
                value={draft.targetColumn}
                onChange={(e) => setDraft({ ...draft, targetColumn: e.target.value })}
              />
            </Field>
            <Field label="Cardinality">
              <Select
                value={String(draft.cardinality)}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    cardinality: Number(e.target.value) as CreateRelationshipPayload['cardinality'],
                  })
                }
              >
                {Object.entries(CARDINALITY_LABEL).map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </Select>
            </Field>
          </CardBody>
          <CardActions>
            <Button variant="ghost" onClick={() => setIsAdding(false)}>
              Cancel
            </Button>
            <Button onClick={submitDraft} disabled={!isDraftComplete || create.isPending}>
              Add
            </Button>
          </CardActions>
        </Card>
      )}

      {proposals.length > 0 && (
        <Card>
          <CardHead>
            <div>
              <CardTitle>Discovered proposals ({proposals.length})</CardTitle>
              <CardSub>Nothing is saved until you accept it</CardSub>
            </div>
            <CardActions>
              <Button variant="ghost" onClick={() => setProposals([])}>
                Dismiss all
              </Button>
            </CardActions>
          </CardHead>
          <CardBody className="space-y-2">
            {proposals.map((proposal) => (
              <div
                key={`${proposal.sourceSchema}.${proposal.sourceTable}.${proposal.sourceColumn}`}
                className="flex flex-wrap items-center justify-between gap-2 rounded-xs border border-border-strong bg-surface-2 px-3 py-2"
              >
                <RelationshipEdge
                  sourceSchema={proposal.sourceSchema}
                  sourceTable={proposal.sourceTable}
                  sourceColumn={proposal.sourceColumn}
                  targetSchema={proposal.targetSchema}
                  targetTable={proposal.targetTable}
                  targetColumn={proposal.targetColumn}
                />
                <div className="flex items-center gap-2">
                  <Pill tone={originTone(proposal.origin)}>{ORIGIN_LABEL[proposal.origin]}</Pill>
                  <span className="mono text-2xs text-text-muted">
                    {proposal.confidence.toFixed(2)}
                  </span>
                  <Button variant="ghost" onClick={() => acceptProposal(proposal)}>
                    <Check className="size-4" aria-hidden />
                    Accept
                  </Button>
                </div>
              </div>
            ))}
          </CardBody>
        </Card>
      )}

      <Card>
        <CardHead>
          <div>
            <CardTitle>Registered relationships</CardTitle>
            <CardSub>
              Unverified relationships still ground joins, but are labelled as guesses in the prompt
            </CardSub>
          </div>
        </CardHead>
        <CardBody>
          {relationships.isLoading && <p className="subtle text-sm">Loading relationships…</p>}

          {!relationships.isLoading && entries.length === 0 && (
            <Banner
              tone="info"
              title="No relationships registered yet"
              sub="Run Discover to seed them from foreign keys and column naming, or add one by hand."
            />
          )}

          {entries.length > 0 && (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-border-strong text-left">
                    <th className="eyebrow py-2">Relationship</th>
                    <th className="eyebrow py-2">Origin</th>
                    <th className="eyebrow py-2">Cardinality</th>
                    <th className="eyebrow py-2">Status</th>
                    <th className="eyebrow py-2 text-right">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry) => (
                    <RelationshipRow
                      key={entry.id}
                      entry={entry}
                      onVerify={(isVerified) => verify.mutate({ id: entry.id, isVerified })}
                      onDelete={() => remove.mutate(entry.id)}
                      isBusy={verify.isPending || remove.isPending}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardBody>
      </Card>
    </div>
  );
}

interface RelationshipEdgeProps {
  sourceSchema: string;
  sourceTable: string;
  sourceColumn: string;
  targetSchema: string;
  targetTable: string;
  targetColumn: string;
}

function RelationshipEdge(props: RelationshipEdgeProps) {
  return (
    <span className="mono flex items-center gap-1.5 text-xs">
      <span>
        {props.sourceSchema}.{props.sourceTable}.{props.sourceColumn}
      </span>
      <ArrowRight className="size-3.5 text-text-muted" aria-hidden />
      <span>
        {props.targetSchema}.{props.targetTable}.{props.targetColumn}
      </span>
    </span>
  );
}

interface RelationshipRowProps {
  entry: SchemaRelationshipEntry;
  onVerify: (isVerified: boolean) => void;
  onDelete: () => void;
  isBusy: boolean;
}

function RelationshipRow({ entry, onVerify, onDelete, isBusy }: RelationshipRowProps) {
  return (
    <tr className="border-b border-border">
      <td className="py-2">
        <RelationshipEdge
          sourceSchema={entry.sourceSchema}
          sourceTable={entry.sourceTable}
          sourceColumn={entry.sourceColumn}
          targetSchema={entry.targetSchema}
          targetTable={entry.targetTable}
          targetColumn={entry.targetColumn}
        />
      </td>
      <td className="py-2">
        <Pill tone={originTone(entry.origin)}>{ORIGIN_LABEL[entry.origin]}</Pill>
      </td>
      <td className="py-2 text-text-muted">{CARDINALITY_LABEL[entry.cardinality]}</td>
      <td className="py-2">
        {entry.isVerified ? (
          <Pill tone="ok">Verified</Pill>
        ) : (
          <Pill tone="warn">Unverified · {entry.confidence.toFixed(2)}</Pill>
        )}
      </td>
      <td className="py-2">
        <div className="flex justify-end gap-1">
          <Button
            variant="ghost"
            aria-label={entry.isVerified ? 'Remove verification' : 'Verify relationship'}
            onClick={() => onVerify(!entry.isVerified)}
            disabled={isBusy}
          >
            {entry.isVerified ? <X className="size-4" aria-hidden /> : <Check className="size-4" aria-hidden />}
          </Button>
          <Button
            variant="ghost"
            aria-label="Delete relationship"
            onClick={onDelete}
            disabled={isBusy}
          >
            <Trash2 className="size-4" aria-hidden />
          </Button>
        </div>
      </td>
    </tr>
  );
}

export default RelationshipsPage;
