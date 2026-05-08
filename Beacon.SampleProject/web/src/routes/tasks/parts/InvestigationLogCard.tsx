import { useState } from 'react';
import { toast } from 'sonner';
import { Icon } from '@/components/Icon';
import { useAuth } from '@/auth/useAuth';
import { ApiError } from '@/lib/api';
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
      const msg = err instanceof ApiError
        ? err.body || `Post failed (${err.status})`
        : err instanceof Error ? err.message : 'Unknown error';
      toast.error(msg);
    }
  };

  return (
    <div className="card">
      <div className="card__head">
        <Icon.Inbox size={15} className="muted" />
        <h3 className="card__title">Investigation log</h3>
        <span className="card__sub">{comments.length} note{comments.length === 1 ? '' : 's'}</span>
      </div>
      <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
        <div className="composer">
          <div className="avatar">{initials(currentUser?.displayName ?? undefined)}</div>
          <div className="composer__main">
            <textarea
              id={textareaId}
              className="composer__input"
              placeholder="Leave a note for whoever picks this up next…"
              value={content}
              onChange={e => setContent(e.target.value.slice(0, MAX_LEN))}
            />
            <div className="composer__bar">
              <span className="muted" style={{ fontSize: 11.5 }}>
                Markdown supported · <span className="mono">{content.length}/{MAX_LEN}</span>
              </span>
              <div style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
                <button
                  type="button"
                  className="btn btn--ghost"
                  onClick={() => setContent('')}
                  disabled={!content || add.isPending}
                >
                  Cancel
                </button>
                <button
                  type="button"
                  className={`btn btn--primary${canPost ? '' : ' is-disabled'}`}
                  onClick={onPost}
                  disabled={!canPost}
                >
                  <Icon.Plus size={13} className="btn__icon" />
                  {add.isPending ? 'Posting…' : 'Post note'}
                </button>
              </div>
            </div>
          </div>
        </div>

        {isLoading && <span className="muted">Loading notes…</span>}

        {!isLoading && comments.length === 0 && (
          <div className="empty-state">
            <div className="empty-state__icon"><Icon.Inbox size={18} /></div>
            <div>
              <div className="empty-state__title">No notes yet</div>
              <div className="empty-state__sub">Capture what you tried, what you found, and how you fixed it.</div>
            </div>
          </div>
        )}

        {comments.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {comments.map(c => (
              <div key={c.id} style={{ display: 'flex', gap: 10 }}>
                <div className="avatar">{initials(c.userName)}</div>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ fontSize: 12.5, color: 'var(--text)' }}>
                    <span className="mono" style={{ fontWeight: 500 }}>{c.userName ?? 'system'}</span>
                    <span className="subtle" style={{ marginLeft: 8, fontSize: 11.5 }}>
                      {formatRelativeTime(c.createdAt)}
                    </span>
                  </div>
                  <div style={{ whiteSpace: 'pre-wrap', fontSize: 13, marginTop: 2, color: 'var(--text)' }}>
                    {c.content}
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
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
