import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { PageHeader, Button, Card, Input, Select, Textarea } from '@/components/beacon';
import { useProjectsQuery } from '../projects/queries';
import { describeMcpError, useMcpTools, useRunMcpTool } from './queries';

interface ChatMessage {
  id: number;
  isUser: boolean;
  text: string;
  toolName: string;
  isError: boolean;
  durationMs: number | null;
}

export default function McpPlaygroundPage() {
  const projectsQ = useProjectsQuery();
  const toolsQ = useMcpTools();
  const runMutation = useRunMcpTool();

  const [projectId, setProjectId] = useState<number | null>(null);
  const [tool, setTool] = useState<string>('ask');
  const [text, setText] = useState('');
  const [datasource, setDatasource] = useState('');
  const [tableName, setTableName] = useState('');
  const [schemaName, setSchemaName] = useState('');
  const [maxResults, setMaxResults] = useState(20);
  const [maxRows, setMaxRows] = useState(100);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const chatRef = useRef<HTMLDivElement>(null);
  const messageIdRef = useRef(0);

  const projects = projectsQ.data?.entries ?? [];
  const toolNames = toolsQ.data?.toolNames ?? [];

  // Auto-select sole project.
  useEffect(() => {
    if (projectId === null && projects.length === 1 && projects[0].id !== undefined) {
      setProjectId(projects[0].id);
    }
  }, [projectId, projects]);

  // Auto-scroll on new message.
  useEffect(() => {
    if (chatRef.current) {
      chatRef.current.scrollTop = chatRef.current.scrollHeight;
    }
  }, [messages.length, runMutation.isPending]);

  function buildArgs(): Record<string, unknown> {
    switch (tool) {
      case 'ask':
        return { question: text, execute: true };
      case 'search':
        return { query: text, max_results: maxResults };
      case 'query': {
        const args: Record<string, unknown> = { sql: text, max_rows: maxRows };
        if (datasource.trim()) args.datasource_name = datasource.trim();
        return args;
      }
      case 'get_documentation': {
        const args: Record<string, unknown> = {};
        if (datasource.trim()) args.datasource_name = datasource.trim();
        if (tableName.trim()) args.table_name = tableName.trim();
        if (schemaName.trim()) args.schema_name = schemaName.trim();
        return args;
      }
      default:
        return {};
    }
  }

  function displayText(): string {
    switch (tool) {
      case 'ask':
        return text;
      case 'search':
        return `search: ${text}`;
      case 'query':
        return `${datasource ? `[${datasource}] ` : ''}${text}`;
      case 'get_documentation':
        return [datasource, tableName, schemaName].filter(Boolean).join(' / ') || 'Project documentation';
      default:
        return 'get_context';
    }
  }

  function send() {
    if (runMutation.isPending) return;
    if (projectId === null) {
      toast.warning('Select a project first.');
      return;
    }
    const args = buildArgs();
    const display = displayText();
    setMessages(prev => [
      ...prev,
      { id: messageIdRef.current++, isUser: true, text: display, toolName: tool, isError: false, durationMs: null },
    ]);
    const startedAt = performance.now();
    runMutation.mutate(
      { toolName: tool, projectId, arguments: args },
      {
        onSuccess: r => {
          const ms = Math.round(performance.now() - startedAt);
          setMessages(prev => [
            ...prev,
            { id: messageIdRef.current++, isUser: false, text: r.text, toolName: tool, isError: r.isError, durationMs: ms },
          ]);
          setText('');
        },
        onError: err => {
          setMessages(prev => [
            ...prev,
            {
              id: messageIdRef.current++,
              isUser: false,
              text: describeMcpError(err, 'Tool execution failed'),
              toolName: tool,
              isError: true,
              durationMs: null,
            },
          ]);
        },
      },
    );
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey && tool !== 'query') {
      e.preventDefault();
      send();
    }
  }

  const canSend =
    !runMutation.isPending &&
    projectId !== null &&
    (tool === 'get_context' || (tool === 'get_documentation' ? true : text.trim().length > 0));

  return (
    <div className="flex flex-col gap-3 p-7 h-[calc(100vh-80px)]">
      <PageHeader
        variant="signal"
        eyebrow="MCP"
        prefix="Exploring"
        emphasis="playground"
        sub="Test MCP tools interactively. Select a project and start asking questions."
        actions={
          <>
            <Select
              value={projectId ?? ''}
              onChange={e => setProjectId(e.target.value ? Number(e.target.value) : null)}
            >
              <option value="">Select project…</option>
              {projects.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </Select>
            <Select value={tool} onChange={e => setTool(e.target.value)}>
              {toolNames.map(t => (
                <option key={t} value={t}>{t}</option>
              ))}
            </Select>
            <Button
              type="button"
              onClick={() => setMessages([])}
              disabled={messages.length === 0}
            >
              Clear
            </Button>
          </>
        }
      />

      <div
        ref={chatRef}
        className="flex-1 overflow-y-auto p-4 flex flex-col gap-3 bg-surface-2 border border-border rounded-md"
      >
        {messages.length === 0 ? (
          <div className="text-text-muted text-center m-auto">
            Select a project and ask a question to get started.
          </div>
        ) : (
          messages.map(m => <MessageBubble key={m.id} msg={m} />)
        )}
        {runMutation.isPending && (
          <div className="text-text-muted">Executing {tool}…</div>
        )}
      </div>

      <Card className="p-3">
        {tool === 'ask' && (
          <div className="flex gap-2">
            <Input
              className="flex-1"
              value={text}
              onChange={e => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Ask a question about your data…"
              disabled={runMutation.isPending}
            />
            <Button variant="primary" type="button" onClick={send} disabled={!canSend}>
              Send
            </Button>
          </div>
        )}
        {tool === 'search' && (
          <div className="flex gap-2">
            <Input
              className="flex-1"
              value={text}
              onChange={e => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Keyword (e.g. customer, revenue)"
              disabled={runMutation.isPending}
            />
            <Input
              type="number"
              min={1}
              max={50}
              value={maxResults}
              onChange={e => setMaxResults(Number(e.target.value))}
              className="w-20"
            />
            <Button variant="primary" type="button" onClick={send} disabled={!canSend}>
              Send
            </Button>
          </div>
        )}
        {tool === 'query' && (
          <div className="flex flex-col gap-2">
            <div className="flex gap-2">
              <Input
                className="flex-1"
                value={datasource}
                onChange={e => setDatasource(e.target.value)}
                placeholder="Data source name"
              />
              <Input
                type="number"
                min={1}
                max={1000}
                value={maxRows}
                onChange={e => setMaxRows(Number(e.target.value))}
                className="w-24"
              />
              <Button variant="primary" type="button" onClick={send} disabled={!canSend}>
                Send
              </Button>
            </div>
            <Textarea
              rows={3}
              value={text}
              onChange={e => setText(e.target.value)}
              placeholder="SELECT * FROM …"
            />
          </div>
        )}
        {tool === 'get_documentation' && (
          <div className="flex gap-2">
            <Input
              className="flex-1"
              value={datasource}
              onChange={e => setDatasource(e.target.value)}
              placeholder="Data source (optional)"
            />
            <Input
              className="flex-1"
              value={tableName}
              onChange={e => setTableName(e.target.value)}
              placeholder="Table (optional)"
            />
            <Input
              className="flex-1"
              value={schemaName}
              onChange={e => setSchemaName(e.target.value)}
              placeholder="Schema (optional)"
            />
            <Button variant="primary" type="button" onClick={send} disabled={!canSend}>
              Send
            </Button>
          </div>
        )}
        {tool === 'get_context' && (
          <div className="flex gap-2 items-center">
            <span className="text-text-muted flex-1">
              No parameters — click send for the project overview.
            </span>
            <Button variant="primary" type="button" onClick={send} disabled={!canSend}>
              Send
            </Button>
          </div>
        )}
      </Card>
    </div>
  );
}

function MessageBubble({ msg }: { msg: ChatMessage }) {
  if (msg.isUser) {
    return (
      <div className="flex justify-end">
        <div
          className="max-w-[75%] p-2.5 bg-brand-50 border border-brand-200 shadow-sm"
          style={{ borderRadius: '12px 12px 0 12px' }}
        >
          <div className="whitespace-pre-wrap text-sm">{msg.text}</div>
          <div className="text-text-muted text-[11px] mt-1">{msg.toolName}</div>
        </div>
      </div>
    );
  }
  return (
    <div className="flex justify-start">
      <div
        className={`max-w-[85%] p-2.5 bg-surface border shadow-sm ${msg.isError ? 'border-crit' : 'border-border'}`}
        style={{ borderRadius: '12px 12px 12px 0' }}
      >
        {msg.isError && (
          <div className="text-crit font-semibold mb-1">Error</div>
        )}
        <pre className="whitespace-pre-wrap break-words m-0 text-[13px] mono">
          {msg.text}
        </pre>
        {msg.durationMs !== null && (
          <div className="text-text-muted text-[11px] mt-1">{msg.durationMs} ms</div>
        )}
      </div>
    </div>
  );
}
