import { useRef, useState } from 'react';
import { toast } from 'sonner';
import { Banner, Button, Field, Input, Pill, Select, Textarea } from '@/components/beacon';
import { Dialog } from '@/components/ui/Dialog';
import { describeError, httpErrorInfo } from '@/lib/api';
import { DatabaseEngineType, DataSourceType } from '@/lib/enums';
import {
  DATABASE_ENGINE_LABEL,
  useTestDataSourceConnection,
  useUpdateDataSource,
  type DataSourceEntry,
} from './queries';

type TestState =
  | { status: 'idle' }
  | { status: 'pending' }
  | { status: 'success'; message: string; revision: number }
  | { status: 'error'; message: string };

/** The server's answer (409) to a connection that points at another target (DataSourceService.TargetChangeUnconfirmedMessage). */
export const TARGET_CHANGE_UNCONFIRMED_MESSAGE =
  'The new connection points at another server, port, database or engine. Confirm the target change to save it: the column values sampled from the old target are removed.';

const ENGINES = Object.entries(DATABASE_ENGINE_LABEL)
  .map(([value, label]) => ({ value: Number(value) as DatabaseEngineType, label }))
  .filter(x => x.value !== DatabaseEngineType.SQLite);

function engineOf(entry: DataSourceEntry): DatabaseEngineType | null {
  return entry.databaseEngineType
    ? DatabaseEngineType[entry.databaseEngineType as keyof typeof DatabaseEngineType] ?? null
    : null;
}

/**
 * Replaces a data source's connection: the way its credentials are rotated. The stored connection is never shown or
 * prefilled. The new one must pass "Test connection" before it can be saved, and testing again is required after any
 * change: a test counts only for the exact input it tested, so an answer that arrives after an edit, or after the dialog
 * closed, is ignored. A connection that points at another server, port, database or engine is saved only after the admin confirms
 * the target change (the server answers 409 until then). The metadata settings are not sent, so they stay as stored.
 */
export function ReplaceConnectionDialog({
  entry,
  open,
  onClose,
}: {
  entry: DataSourceEntry;
  open: boolean;
  onClose: () => void;
}) {
  const isDatabase = entry.dataSourceType === 'Database';
  const [engine, setEngine] = useState<DatabaseEngineType | null>(engineOf(entry));
  const [connection, setConnection] = useState('');
  const [test, setTest] = useState<TestState>({ status: 'idle' });
  const [targetChange, setTargetChange] = useState<string | null>(null);
  const [confirmTarget, setConfirmTarget] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const testMutation = useTestDataSourceConnection();
  const updateMutation = useUpdateDataSource();

  // Every change to what would be saved, and closing the dialog, starts a new input revision; a test's answer is kept
  // only while the revision it tested is still the current one.
  const revision = useRef(0);
  const [currentRevision, setCurrentRevision] = useState(0);

  // Any change to what would be saved needs a fresh test and a fresh confirmation.
  const edited = () => {
    revision.current += 1;
    setCurrentRevision(revision.current);
    setTest({ status: 'idle' });
    setTargetChange(null);
    setConfirmTarget(false);
    setError(null);
  };

  const close = () => {
    setEngine(engineOf(entry));
    setConnection('');
    edited();
    onClose();
  };

  const onTest = async () => {
    const tested = revision.current;
    setTest({ status: 'pending' });
    setError(null);
    let answer: TestState;
    try {
      const result = await testMutation.mutateAsync({
        name: entry.name,
        dataSourceType: DataSourceType[entry.dataSourceType as keyof typeof DataSourceType],
        databaseEngineType: isDatabase ? engine : null,
        connectionString: connection,
      });
      answer = result.success
        ? { status: 'success', message: result.message ?? 'Connected.', revision: tested }
        : { status: 'error', message: result.message ?? 'Connection failed. The server returned no reason.' };
    } catch (err) {
      answer = { status: 'error', message: describeError(err, 'Connection test failed') };
    }

    // The input changed, or the dialog closed, while the test ran: its answer is about another input.
    if (revision.current === tested) {
      setTest(answer);
    }
  };

  const onSave = async () => {
    setError(null);
    try {
      await updateMutation.mutateAsync({
        id: entry.id,
        name: entry.name,
        databaseEngineType: isDatabase ? engine : null,
        connectionString: connection,
        confirmTargetChange: confirmTarget,
      });
      toast.success(`Replaced the connection of '${entry.name}'.`);
      close();
    } catch (err) {
      // 409: the new connection points somewhere else; the admin confirms the change and saves again.
      const message = describeError(err, '');
      if (httpErrorInfo(err)?.status === 409 && !confirmTarget && message === TARGET_CHANGE_UNCONFIRMED_MESSAGE) {
        setTargetChange(message);
        return;
      }

      setError(describeError(err, 'Replacing the connection failed'));
    }
  };

  const canTest = connection.trim() !== '' && (!isDatabase || engine != null) && test.status !== 'pending';
  const canSave = test.status === 'success'
    && test.revision === currentRevision
    && (targetChange === null || confirmTarget) && !updateMutation.isPending;

  return (
    <Dialog
      open={open}
      onClose={close}
      title="Replace connection"
      sub={<>Rotate the credentials of <strong>{entry.name}</strong>. The stored connection is never shown.</>}
      footer={
        <>
          <Button onClick={close} disabled={updateMutation.isPending}>Cancel</Button>
          <Button variant="primary" onClick={onSave} disabled={!canSave}>
            {updateMutation.isPending ? 'Saving…' : 'Save'}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-4">
        {isDatabase && (
          <Field label="Database engine">
            <Select
              aria-label="Database engine"
              value={engine ?? ''}
              onChange={e => {
                setEngine(Number(e.target.value) as DatabaseEngineType);
                edited();
              }}
            >
              {ENGINES.map(x => (
                <option key={x.value} value={x.value}>{x.label}</option>
              ))}
            </Select>
          </Field>
        )}

        <Field
          label={isDatabase ? 'New connection string' : 'New connection data'}
          hint="Encrypted at rest. It must make the driver verify the server over TLS."
        >
          {isDatabase
            ? (
              <Input
                aria-label="New connection string"
                type="password"
                className="mono"
                autoComplete="off"
                value={connection}
                onChange={e => {
                  setConnection(e.target.value);
                  edited();
                }}
              />
            )
            : (
              <Textarea
                aria-label="New connection data"
                className="mono"
                rows={5}
                autoComplete="off"
                spellCheck={false}
                value={connection}
                onChange={e => {
                  setConnection(e.target.value);
                  edited();
                }}
              />
            )}
        </Field>

        <div className="flex items-center gap-2.5">
          <Button onClick={onTest} disabled={!canTest}>
            {test.status === 'pending' ? 'Testing…' : 'Test connection'}
          </Button>
          {test.status === 'success' && <Pill tone="ok">Connected</Pill>}
          {test.status === 'error' && <Pill tone="crit">Failed</Pill>}
          {test.status === 'idle' && <span className="text-xs text-text-muted">Test the new connection before saving.</span>}
        </div>
        {test.status === 'error' && (
          <div className="text-xs text-crit whitespace-pre-wrap break-words">{test.message}</div>
        )}

        {targetChange !== null && (
          <Banner
            tone="warn"
            title="Target change"
            sub={
              <>
                <span className="block">{targetChange}</span>
                <label className="mt-2 flex items-center gap-2 text-sm text-text">
                  <input
                    type="checkbox"
                    checked={confirmTarget}
                    onChange={e => setConfirmTarget(e.target.checked)}
                  />
                  Confirm the target change
                </label>
              </>
            }
          />
        )}

        {error !== null && <Banner tone="crit" title="Not saved" sub={error} />}
      </div>
    </Dialog>
  );
}
