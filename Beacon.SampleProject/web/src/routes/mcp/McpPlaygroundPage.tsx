import { useEffect, useRef, useState } from 'react';
import { toast } from 'sonner';
import { PageHeader } from '@/components/layout/PageHeader';
import { useProjectsQuery } from '../projects/queries';
import { describeMcpError, useMcpTools, useRunMcpTool } from './queries';

interface ChatMessage {
  isUser: boolean;
  text: string;
  toolName: string;
  isError: boolean;
  durationMs: number | null;
}

const DEFAULT_TOOLS = ['ask', 'search', 'query', 'get_documentation', 'get_context'];

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

  const projects = projectsQ.data?.entries ?? [];
  const toolNames = toolsQ.data?.toolNames ?? DEFAULT_TOOLS;

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
    if (projectId === null) {
      toast.warning('Select a project first.');
      return;
    }
    const args = buildArgs();
    const display = displayText();
    setMessages(prev => [
      ...prev,
      { isUser: true, text: display, toolName: tool, isError: false, durationMs: null },
    ]);
    const startedAt = performance.now();
    runMutation.mutate(
      { toolName: tool, projectId, arguments: args },
      {
        onSuccess: r => {
          const ms = Math.round(performance.now() - startedAt);
          setMessages(prev => [
            ...prev,
            { isUser: false, text: r.text, toolName: tool, isError: r.isError, durationMs: ms },
          ]);
          setText('');
        },
        onError: err => {
          setMessages(prev => [
            ...prev,
            {
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
    <div className="page" style={{ display: 'flex', flexDirection: 'column', height: 'calc(100vh - 80px)' }}>
      <PageHeader
        title="MCP playground"
        sub="Test MCP tools interactively. Select a project and start asking questions."
        actions={
          <>
            <select
              className="select"
              value={projectId ?? ''}
              onChange={e => setProjectId(e.target.value ? Number(e.target.value) : null)}
            >
              <option value="">Select project…</option>
              {projects.map(p => (
                <option key={p.id} value={p.id}>{p.name}</option>
              ))}
            </select>
            <select className="select" value={tool} onChange={e => setTool(e.target.value)}>
              {toolNames.map(t => (
                <option key={t} value={t}>{t}</option>
              ))}
            </select>
            <button
              type="button"
              className="btn"
              onClick={() => setMessages([])}
              disabled={messages.length === 0}
            >
              Clear
            </button>
          </>
        }
      />

      <div
        ref={chatRef}
        className="card"
        style={{
          flex: 1,
          overflowY: 'auto',
          padding: 16,
          display: 'flex',
          flexDirection: 'column',
          gap: 12,
          background: 'var(--surface-2, var(--surface))',
        }}
      >
        {messages.length === 0 ? (
          <div className="muted" style={{ textAlign: 'center', margin: 'auto' }}>
            Select a project and ask a question to get started.
          </div>
        ) : (
          messages.map((m, i) => <MessageBubble key={i} msg={m} />)
        )}
        {runMutation.isPending && (
          <div className="muted">Executing {tool}…</div>
        )}
      </div>

      <div className="card" style={{ marginTop: 8, padding: 12 }}>
        {tool === 'ask' && (
          <div style={{ display: 'flex', gap: 8 }}>
            <input
              className="input"
              style={{ flex: 1 }}
              value={text}
              onChange={e => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Ask a question about your data…"
              disabled={runMutation.isPending}
            />
            <button type="button" className="btn btn--primary" onClick={send} disabled={!canSend}>
              Send
            </button>
          </div>
        )}
        {tool === 'search' && (
          <div style={{ display: 'flex', gap: 8 }}>
            <input
              className="input"
              style={{ flex: 1 }}
              value={text}
              onChange={e => setText(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Keyword (e.g. customer, revenue)"
            />
            <input
              className="input"
              type="number"
              min={1}
              max={50}
              value={maxResults}
              onChange={e => setMaxResults(Number(e.target.value))}
              style={{ width: 80 }}
            />
            <button type="button" className="btn btn--primary" onClick={send} disabled={!canSend}>
              Send
            </button>
          </div>
        )}
        {tool === 'query' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div style={{ display: 'flex', gap: 8 }}>
              <input
                className="input"
                style={{ flex: 1 }}
                value={datasource}
                onChange={e => setDatasource(e.target.value)}
                placeholder="Data source name"
              />
              <input
                className="input"
                type="number"
                min={1}
                max={1000}
                value={maxRows}
                onChange={e => setMaxRows(Number(e.target.value))}
                style={{ width: 100 }}
              />
              <button type="button" className="btn btn--primary" onClick={send} disabled={!canSend}>
                Send
              </button>
            </div>
            <textarea
              className="textarea"
              rows={3}
              value={text}
              onChange={e => setText(e.target.value)}
              placeholder="SELECT * FROM …"
            />
          </div>
        )}
        {tool === 'get_documentation' && (
          <div style={{ display: 'flex', gap: 8 }}>
            <input
              className="input"
              style={{ flex: 1 }}
              value={datasource}
              onChange={e => setDatasource(e.target.value)}
              placeholder="Data source (optional)"
            />
            <input
              className="input"
              style={{ flex: 1 }}
              value={tableName}
              onChange={e => setTableName(e.target.value)}
              placeholder="Table (optional)"
            />
            <input
              className="input"
              style={{ flex: 1 }}
              value={schemaName}
              onChange={e => setSchemaName(e.target.value)}
              placeholder="Schema (optional)"
            />
            <button type="button" className="btn btn--primary" onClick={send} disabled={!canSend}>
              Send
            </button>
          </div>
        )}
        {tool === 'get_context' && (
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <span className="muted" style={{ flex: 1 }}>
              No parameters — click send for the project overview.
            </span>
            <button type="button" className="btn btn--primary" onClick={send} disabled={!canSend}>
              Send
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

function MessageBubble({ msg }: { msg: ChatMessage }) {
  if (msg.isUser) {
    return (
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <div
          className="card"
          style={{
            maxWidth: '75%',
            padding: 10,
            background: 'var(--brand-50)',
            borderColor: 'var(--brand-200)',
            borderRadius: '12px 12px 0 12px',
          }}
        >
          <div style={{ whiteSpace: 'pre-wrap' }}>{msg.text}</div>
          <div className="muted" style={{ fontSize: 11, marginTop: 4 }}>{msg.toolName}</div>
        </div>
      </div>
    );
  }
  return (
    <div style={{ display: 'flex', justifyContent: 'flex-start' }}>
      <div
        className="card"
        style={{
          maxWidth: '85%',
          padding: 10,
          borderRadius: '12px 12px 12px 0',
          borderColor: msg.isError ? 'var(--crit)' : undefined,
        }}
      >
        {msg.isError && (
          <div style={{ color: 'var(--crit)', fontWeight: 600, marginBottom: 4 }}>Error</div>
        )}
        <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word', margin: 0, fontSize: 13 }}>
          {msg.text}
        </pre>
        {msg.durationMs !== null && (
          <div className="muted" style={{ fontSize: 11, marginTop: 4 }}>{msg.durationMs} ms</div>
        )}
      </div>
    </div>
  );
}
