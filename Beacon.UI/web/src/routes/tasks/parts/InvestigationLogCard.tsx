import { useState } from 'react';
import { toast } from 'sonner';
import { Inbox, Plus } from 'lucide-react';
import {
  Button,
  Card,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  Textarea,
} from '@/components/beacon';
import { useAuth } from '@/auth/useAuth';
import { describeError } from '@/lib/api';
import { formatRelativeTime } from '@/lib/format';
import { useAddTaskComment, useTaskCommentsQuery } from '../queries';

const MAX_LEN = 2000;

export function InvestigationLogCard({ taskId, textareaId }: { taskId: number; textareaId?: string }) {
  const [content, setContent] = useState('');
  const { data, isLoading } = useTaskCommentsQuery(taskId);
  const add = useAddTaskComment(taskId);
  const { data: currentUser } = useAuth();

  const comments = data?.comments ?? [];
  const trimmed = content.trim();
  const canPost = trimmed.length > 0 && trimmed.length <= MAX_LEN && !add.isPending;

  const onPost = async () => {
    if (!canPost) return;
    try {
      await add.mutateAsync(trimmed);
      setContent('');
      toast.success('Note posted');
    } catch (err) {
            toast.error(describeError(err, 'Post failed'));
    }
  };

  return (
    <Card>
      <CardHead>
        <Inbox className="size-3.5 text-text-muted" />
        <CardTitle>Investigation log</CardTitle>
        <CardSub>{comments.length} note{comments.length === 1 ? '' : 's'}</CardSub>
      </CardHead>
      <CardBody className="flex flex-col gap-3.5">
        <div className="flex gap-2.5">
          <Avatar name={currentUser?.displayName ?? undefined} />
          <div className="flex-1 min-w-0 flex flex-col gap-1.5">
            <Textarea
              id={textareaId}
              placeholder="Leave a note for whoever picks this up next…"
              value={content}
              onChange={e => setContent(e.target.value.slice(0, MAX_LEN))}
            />
            <div className="flex items-center gap-2">
              <span className="text-text-muted text-xs">
                Markdown supported · <span className="mono">{content.length}/{MAX_LEN}</span>
              </span>
              <div className="ml-auto flex gap-1.5">
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => setContent('')}
                  disabled={!content || add.isPending}
                >
                  Cancel
                </Button>
                <Button
                  variant="primary"
                  size="sm"
                  icon={<Plus />}
                  onClick={onPost}
                  disabled={!canPost}
                >
                  {add.isPending ? 'Posting…' : 'Post note'}
                </Button>
              </div>
            </div>
          </div>
        </div>

        {isLoading && <span className="text-text-muted">Loading notes…</span>}

        {!isLoading && comments.length === 0 && (
          <div className="flex items-start gap-3 p-5 rounded-md border border-dashed border-border bg-surface-2">
            <div className="shrink-0 size-9 grid place-items-center rounded-sm bg-surface text-text-muted [&>svg]:size-4">
              <Inbox />
            </div>
            <div className="flex-1 min-w-0">
              <div className="text-sm font-medium text-text">No notes yet</div>
              <div className="text-xs text-text-muted mt-0.5">
                Capture what you tried, what you found, and how you fixed it.
              </div>
            </div>
          </div>
        )}

        {comments.length > 0 && (
          <ul className="flex flex-col gap-3 list-none p-0 m-0">
            {comments.map(c => (
              <li key={c.id} className="flex gap-2.5">
                <Avatar name={c.userName ?? undefined} />
                <div className="flex-1 min-w-0">
                  <div className="text-sm text-text">
                    <span className="mono font-medium">{c.userName ?? 'system'}</span>
                    <span className="text-text-subtle ml-2 text-xs">
                      {formatRelativeTime(c.createdAt)}
                    </span>
                  </div>
                  <div className="whitespace-pre-wrap text-sm mt-0.5 text-text">
                    {c.content}
                  </div>
                </div>
              </li>
            ))}
          </ul>
        )}
      </CardBody>
    </Card>
  );
}

function Avatar({ name }: { name?: string | null }) {
  return (
    <div className="shrink-0 size-8 grid place-items-center rounded-sm bg-brand-100 text-brand-700 text-2xs font-semibold uppercase tracking-eyebrow">
      {initials(name)}
    </div>
  );
}

function initials(name?: string | null): string {
  if (!name) return '·';
  const parts = name.trim().split(/\s+/);
  const first = parts[0]?.[0] ?? '';
  const second = parts[1]?.[0] ?? '';
  return (first + second).toUpperCase() || '·';
}
