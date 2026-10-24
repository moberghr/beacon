import { useMemo } from 'react';
import { AlertTriangle, RotateCcw, ScrollText } from 'lucide-react';
import { DataTable, type Column } from '@/components/data/DataTable';
import { EmptyState } from '@/components/data/EmptyState';
import { Banner, Button, Card, CardActions, CardBody, CardHead, CardSub, CardTitle, Field, Input, PageHeader, Pill } from '@/components/beacon';
import { useRequirePermission } from '@/auth/useRequirePermission';
import { formatDateTime, formatNumber } from '@/lib/format';
import { useSearchFilter } from '@/lib/usePagedList';
import { MAX_WINDOW_DAYS, defaultWindow } from '../security-audit/queries';
import { AuditExportButton } from '../security-audit/AuditExportButton';
import { useMcpAuditList, type McpAuditLogItem } from './mcpAuditQueries';

const GRID_TEMPLATE = '1.2fr 1.2fr 0.6fr 1.4fr 0.6fr 0.7fr 0.6fr 1.4fr';

/** For an Auditor (an Admin holds the permission); the server refuses everyone else. */
export default function McpAuditPage() {
  const allowed = useRequirePermission('Auditor');

  if (allowed === undefined) {
    return (
      <div className="flex flex-col gap-3 p-7">
        <PageHeader variant="signal" eyebrow="MCP" prefix="MCP" emphasis="audit" sub={<span className="text-text-muted">Loading…</span>} />
      </div>
    );
  }

  if (allowed === false) {
    return null;
  }

  return <McpAuditView />;
}

function McpAuditView() {
  const defaults = useMemo(() => defaultWindow(), []);
  const list = useMcpAuditList(defaults);
  const { filters, setFilter, resetFilters, auditWindow } = list;
  const [toolInput, setToolInput] = useSearchFilter(filters.tool, value => setFilter('tool', value));
  const [projectInput, setProjectInput] = useSearchFilter(filters.projectId, value => setFilter('projectId', value));
  const [userInput, setUserInput] = useSearchFilter(filters.userId, value => setFilter('userId', value));
  const [callerInput, setCallerInput] = useSearchFilter(filters.callerHash, value => setFilter('callerHash', value));

  const columns: Column<McpAuditLogItem>[] = [
    { key: 'time', header: 'Time', render: x => <span className="text-text-muted">{formatDateTime(x.createdTime)}</span> },
    { key: 'tool', header: 'Tool', render: x => <span className="mono text-xs text-text truncate">{x.tool}</span> },
    {
      key: 'project',
      header: 'Project',
      render: x => x.projectId != null ? <span className="mono text-xs">#{x.projectId}</span> : <span className="text-text-muted">—</span>,
    },
    {
      key: 'caller',
      header: 'Caller',
      render: x => (
        <div className="flex flex-col min-w-0">
          <span className="text-text truncate">{x.callerKind ?? '—'}</span>
          {x.callerHash && <span className="mono text-2xs text-text-subtle truncate" title={x.callerHash}>{x.callerHash}</span>}
        </div>
      ),
    },
    {
      key: 'user',
      header: 'User',
      render: x => x.userId != null ? <span className="mono text-xs">#{x.userId}</span> : <span className="text-text-muted">—</span>,
    },
    { key: 'duration', header: 'Duration', render: x => <span className="mono text-xs">{formatNumber(x.executionTimeMs)} ms</span> },
    { key: 'rows', header: 'Rows', render: x => <span className="mono text-xs">{formatNumber(x.resultRowCount)}</span> },
    {
      key: 'outcome',
      header: 'Outcome',
      render: x => x.errorMessage
        ? (
          <div className="flex flex-col min-w-0 gap-0.5">
            <Pill tone="crit">Error</Pill>
            <span className="text-2xs text-text-muted truncate" title={x.errorMessage}>{x.errorMessage}</span>
          </div>
        )
        : <Pill tone="ok">Success</Pill>,
    },
  ];

  return (
    <div className="flex flex-col gap-5 p-7">
      <PageHeader
        variant="signal"
        eyebrow="MCP"
        prefix="MCP"
        emphasis="audit"
        sub="What agents did through the MCP server: which tool, as whom, in which project, and with what outcome. Reading the audit is recorded too."
      />

      <Card>
        <CardBody>
          <div className="grid gap-3 grid-cols-2 md:grid-cols-3">
            <Field label="From (UTC)" htmlFor="mcp-audit-from">
              <Input id="mcp-audit-from" type="date" value={filters.from} onChange={e => setFilter('from', e.target.value)} />
            </Field>
            <Field label="To (UTC)" htmlFor="mcp-audit-to">
              <Input id="mcp-audit-to" type="date" value={filters.to} onChange={e => setFilter('to', e.target.value)} />
            </Field>
            <Field label="Tool" htmlFor="mcp-audit-tool">
              <Input id="mcp-audit-tool" placeholder="any" value={toolInput} onChange={e => setToolInput(e.target.value)} />
            </Field>
            <Field label="Project id" htmlFor="mcp-audit-project">
              <Input
                id="mcp-audit-project"
                type="number"
                min={1}
                inputMode="numeric"
                placeholder="any"
                value={projectInput}
                onChange={e => setProjectInput(e.target.value)}
              />
            </Field>
            <Field label="User id" htmlFor="mcp-audit-user">
              <Input
                id="mcp-audit-user"
                type="number"
                min={1}
                inputMode="numeric"
                placeholder="any"
                value={userInput}
                onChange={e => setUserInput(e.target.value)}
              />
            </Field>
            <Field label="Caller hash" htmlFor="mcp-audit-caller">
              <Input id="mcp-audit-caller" placeholder="any" value={callerInput} onChange={e => setCallerInput(e.target.value)} />
            </Field>
          </div>
          <div className="flex items-center justify-between gap-3 mt-3">
            <span className="text-xs text-text-muted">
              The window runs over whole UTC days, at most {MAX_WINDOW_DAYS} at a time; times are shown in your time zone.
            </span>
            <Button type="button" size="sm" variant="ghost" icon={<RotateCcw />} onClick={resetFilters}>
              Reset filters
            </Button>
          </div>
        </CardBody>
      </Card>

      {auditWindow.error !== undefined && (
        <Banner tone="warn" icon={<AlertTriangle />} title="The window cannot be read" sub={auditWindow.error} />
      )}

      <Card>
        <CardHead>
          <CardTitle>Tool calls</CardTitle>
          <CardSub>{formatNumber(list.totalCount)} in the window</CardSub>
          <CardActions>
            <AuditExportButton
              fileName={`mcp-audit-${filters.from}_${filters.to}.json`}
              fetchPage={list.fetchExportPage}
              firstPage={1}
              confirmMessage="Downloads the columns shown here (time, tool, project, caller, user, duration, rows, outcome) for every call of the window as filtered (up to 5000) as an unencrypted JSON file. Parameters and SQL are not included. Each page read is recorded in the audit."
              disabled={auditWindow.error !== undefined || list.totalCount === 0}
            />
          </CardActions>
        </CardHead>
        {list.isError
          ? (
            <EmptyState
              icon={<AlertTriangle />}
              title="The MCP audit could not be read"
              description={list.error instanceof Error ? list.error.message : 'Unknown error'}
            />
          )
          : (
            <DataTable
              ariaLabel="MCP audit"
              columns={columns}
              rows={list.items}
              paging={list.paging}
              loading={list.loading}
              rowKey={x => x.id}
              gridTemplate={GRID_TEMPLATE}
              empty={
                <EmptyState
                  icon={<ScrollText />}
                  title={list.isLoading ? 'Loading calls…' : 'No tool calls'}
                  description={list.isLoading || auditWindow.error !== undefined ? '' : 'Nothing was recorded in this window with these filters.'}
                />
              }
            />
          )}
      </Card>
    </div>
  );
}
