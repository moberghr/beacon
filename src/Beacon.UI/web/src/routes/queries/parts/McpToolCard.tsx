import { useEffect, useId, useState, type FormEvent } from 'react';
import { Plug } from 'lucide-react';
import { Button, Card, CardBody, CardHead, CardSub, CardTitle, Field, Input, Pill, Textarea } from '@/components/beacon';
import { useIsAdmin } from '@/auth/useAuth';
import { MCP_TOOL_NAME_PATTERN, useSetQueryMcpTool, type QueryDetail } from '../queries';

interface McpToolCardProps {
  query: QueryDetail;
}

const MAX_DESCRIPTION = 1000;

/**
 * Exposes the query as the MCP tool `q_<name>`. Only an approved active
 * version ever runs, so the card says which version that is — or why there
 * is none. Admins edit; everyone else sees the state read-only.
 */
export function McpToolCard({ query }: McpToolCardProps) {
  const isAdmin = useIsAdmin();
  const setTool = useSetQueryMcpTool(query.id);
  const formId = useId();

  const [enabled, setEnabled] = useState(query.mcpToolEnabled);
  const [name, setName] = useState(query.mcpToolName ?? '');
  const [description, setDescription] = useState(query.mcpToolDescription ?? '');

  useEffect(() => {
    setEnabled(query.mcpToolEnabled);
    setName(query.mcpToolName ?? '');
    setDescription(query.mcpToolDescription ?? '');
  }, [query.mcpToolEnabled, query.mcpToolName, query.mcpToolDescription]);

  const runnable = query.mcpToolRunnableVersionNumber != null && query.mcpToolIssue == null;
  const trimmedName = name.trim();
  const trimmedDescription = description.trim();
  const nameError =
    trimmedName.length > 0 && !MCP_TOOL_NAME_PATTERN.test(trimmedName)
      ? 'Use 3–41 lowercase letters, digits or underscores, starting with a letter.'
      : enabled && trimmedName.length === 0
        ? 'A name is required to expose the tool.'
        : null;
  const cannotEnable = enabled && !runnable;
  const dirty =
    enabled !== query.mcpToolEnabled ||
    trimmedName !== (query.mcpToolName ?? '') ||
    trimmedDescription !== (query.mcpToolDescription ?? '');

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (nameError || cannotEnable) return;
    try {
      await setTool.mutateAsync({
        enabled,
        name: trimmedName.length > 0 ? trimmedName : null,
        description: trimmedDescription.length > 0 ? trimmedDescription : null,
      });
    } catch {
      // error already toasted by the mutation
    }
  };

  return (
    <Card>
      <CardHead>
        <Plug className="size-3.5 text-text-muted" />
        <CardTitle>MCP tool</CardTitle>
        <CardSub>saved query as an agent tool</CardSub>
        <div className="ml-auto">
          {query.mcpToolEnabled && runnable ? (
            <Pill tone="ok" dot>
              Exposed
            </Pill>
          ) : query.mcpToolEnabled ? (
            <Pill tone="warn" dot>
              Enabled, not callable
            </Pill>
          ) : (
            <Pill tone="neutral">Not exposed</Pill>
          )}
        </div>
      </CardHead>
      <CardBody className="flex flex-col gap-3">
        <div className="text-sm" data-testid="mcp-tool-runnable">
          {runnable ? (
            <span>
              Calls run approved version <span className="mono">v{query.mcpToolRunnableVersionNumber}</span>,
              read-only, with arguments bound as parameters.
            </span>
          ) : (
            <span className="text-warn">{query.mcpToolIssue ?? 'No approved version can run as a tool.'}</span>
          )}
        </div>

        {isAdmin ? (
          <form className="flex flex-col gap-3" onSubmit={onSubmit} noValidate>
            <label className="flex items-center gap-2 text-sm">
              <input
                type="checkbox"
                checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)}
                aria-label="Expose as MCP tool"
              />
              Expose as MCP tool
            </label>
            <Field
              label="Tool name"
              htmlFor={`${formId}-name`}
              hint={
                nameError ??
                (trimmedName ? <span className="mono">q_{trimmedName}</span> : 'Listed to agents as q_<name>.')
              }
            >
              <Input
                id={`${formId}-name`}
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="loan_book_by_month"
                maxLength={41}
                aria-invalid={nameError != null}
                className="mono"
              />
            </Field>
            <Field
              label="Description"
              htmlFor={`${formId}-description`}
              hint="What the tool answers, as the agent reads it. Defaults to the query description."
            >
              <Textarea
                id={`${formId}-description`}
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                maxLength={MAX_DESCRIPTION}
              />
            </Field>
            {cannotEnable && <div className="text-xs text-warn">Approve a version of this query before exposing it.</div>}
            <div>
              <Button
                type="submit"
                variant="primary"
                size="sm"
                disabled={!dirty || nameError != null || cannotEnable || setTool.isPending}
              >
                {setTool.isPending ? 'Saving…' : 'Save'}
              </Button>
            </div>
          </form>
        ) : (
          query.mcpToolName && (
            <div className="flex flex-col gap-1 text-sm">
              <span className="mono">q_{query.mcpToolName}</span>
              {query.mcpToolDescription && <span className="text-text-muted">{query.mcpToolDescription}</span>}
            </div>
          )
        )}
      </CardBody>
    </Card>
  );
}
