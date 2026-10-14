import { beforeEach, describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { toast } from 'sonner';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { ProjectRole } from '@/lib/enums';
import { ADMIN_ONLY_MEMBERSHIP_NOTE, MembersTab } from './MembersTab';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function members(items: Array<Record<string, unknown>>) {
  return http.get('*/beacon/api/projects/5/members', () =>
    HttpResponse.json({ items, totalCount: items.length, pageCount: items.length === 0 ? 0 : 1 }),
  );
}

const ada = {
  userId: 3,
  displayName: 'Ada',
  role: ProjectRole.ProjectAdmin,
  isActive: true,
  addedAt: '2026-10-01T10:00:00Z',
  modifiedAt: null,
  canChange: true,
};

const conflict = (detail: string) =>
  HttpResponse.json({ title: 'Conflict', status: 409, detail }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } });

describe('MembersTab', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
  });

  it('searches people from two characters and adds one with the chosen role', async () => {
    let put: { url: string; body: unknown } | null = null;
    let searched = '';
    mswServer.use(
      members([ada]),
      http.get('*/beacon/api/projects/5/member-candidates', ({ request }) => {
        searched = new URL(request.url).searchParams.get('search') ?? '';
        return HttpResponse.json({
          candidates: [
            { userId: 3, displayName: 'Ada', isMember: true, canAdd: true },
            { userId: 4, displayName: 'Grace', isMember: false, canAdd: true },
          ],
        });
      }),
      http.put('*/beacon/api/projects/5/members/4', async ({ request }) => {
        put = { url: request.url, body: await request.json() };
        return HttpResponse.json({ projectId: 5, userId: 4, role: ProjectRole.Editor, added: true });
      }),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    const search = screen.getByRole('searchbox', { name: /search people to add/i });
    fireEvent.change(search, { target: { value: 'g' } });
    expect(await screen.findByText(/at least 2 characters/i)).toBeInTheDocument();

    fireEvent.change(search, { target: { value: 'gr' } });
    fireEvent.click(within(screen.getByRole('radiogroup', { name: /role for the new member/i })).getByRole('radio', { name: 'Editor' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Add Grace' }));

    await waitFor(() => expect(put?.body).toEqual({ role: ProjectRole.Editor }));
    expect(searched).toBe('gr');
    // An existing member is not offered again.
    expect(screen.getByText('Already a member')).toBeInTheDocument();
  });

  it('changes a role and removes a member after confirming', async () => {
    let roleBody: unknown = null;
    let deleted = false;
    mswServer.use(
      members([ada, { ...ada, userId: 6, displayName: 'Linus', role: ProjectRole.Viewer }]),
      http.put('*/beacon/api/projects/5/members/6', async ({ request }) => {
        roleBody = await request.json();
        return HttpResponse.json({ projectId: 5, userId: 6, role: ProjectRole.Editor, added: false });
      }),
      http.delete('*/beacon/api/projects/5/members/6', () => {
        deleted = true;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    const roles = await screen.findByRole('radiogroup', { name: 'Role of Linus' });
    fireEvent.click(within(roles).getByRole('radio', { name: 'Editor' }));
    await waitFor(() => expect(roleBody).toEqual({ role: ProjectRole.Editor }));

    fireEvent.click(screen.getByRole('button', { name: 'Remove Linus' }));
    fireEvent.click(await screen.findByRole('button', { name: /^remove$/i }));
    await waitFor(() => expect(deleted).toBe(true));
  });

  it('shows a membership the caller may not change read-only, without a remove', async () => {
    mswServer.use(members([ada, { ...ada, userId: 6, displayName: 'Linus', role: ProjectRole.Editor, canChange: false }]));

    renderWithProviders(<MembersTab projectId={5} />);

    expect(await screen.findByText(ADMIN_ONLY_MEMBERSHIP_NOTE)).toBeInTheDocument();
    expect(screen.queryByRole('radiogroup', { name: 'Role of Linus' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove Linus' })).not.toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Role of Ada' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove Ada' })).toBeInTheDocument();
  });

  it('offers no add for a person the caller may not add, and says why', async () => {
    mswServer.use(
      members([ada]),
      http.get('*/beacon/api/projects/5/member-candidates', () =>
        HttpResponse.json({
          candidates: [
            { userId: 4, displayName: 'Grace', isMember: false, canAdd: false },
            { userId: 7, displayName: 'Greta', isMember: false, canAdd: true },
          ],
        }),
      ),
    );

    renderWithProviders(<MembersTab projectId={5} />);
    fireEvent.change(screen.getByRole('searchbox', { name: /search people to add/i }), { target: { value: 'gr' } });

    expect(await screen.findByRole('button', { name: 'Add Greta' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add Grace' })).not.toBeInTheDocument();
    expect(screen.getByText('Only an Admin can add this user.')).toBeInTheDocument();
  });

  it('warns when the project has no active project admin', async () => {
    mswServer.use(members([{ ...ada, role: ProjectRole.Editor }, { ...ada, userId: 7, displayName: 'Old admin', isActive: false }]));

    renderWithProviders(<MembersTab projectId={5} />);

    expect(await screen.findByText(/no active project admin/i)).toBeInTheDocument();
    expect(screen.getByText('Inactive')).toBeInTheDocument();
  });

  it('starts empty', async () => {
    mswServer.use(members([]));

    renderWithProviders(<MembersTab projectId={5} />);

    expect(await screen.findByText('No members yet')).toBeInTheDocument();
  });

  it('does not warn about a missing project admin while other pages are not loaded', async () => {
    mswServer.use(
      http.get('*/beacon/api/projects/5/members', () =>
        HttpResponse.json({ items: [{ ...ada, role: ProjectRole.Editor }], totalCount: 30, pageCount: 2 }),
      ),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    expect(await screen.findByText('Ada')).toBeInTheDocument();
    expect(screen.queryByText(/no active project admin/i)).not.toBeInTheDocument();
  });

  it('sends nothing when the current role is clicked again', async () => {
    let puts = 0;
    mswServer.use(
      members([ada, { ...ada, userId: 6, displayName: 'Linus', role: ProjectRole.Viewer }]),
      http.put('*/beacon/api/projects/5/members/6', () => {
        puts += 1;
        return HttpResponse.json({ projectId: 5, userId: 6, role: ProjectRole.Viewer, added: false });
      }),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    const roles = await screen.findByRole('radiogroup', { name: 'Role of Linus' });
    fireEvent.click(within(roles).getByRole('radio', { name: 'Viewer' }));

    // Give a stray request time to arrive before asserting there was none.
    await new Promise(resolve => setTimeout(resolve, 50));
    expect(puts).toBe(0);
  });

  it('keeps the role and says why when the server refuses a role change', async () => {
    mswServer.use(
      members([ada]),
      http.put('*/beacon/api/projects/5/members/3', () =>
        conflict('A project keeps at least one active project admin; add another before changing this one.'),
      ),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    const roles = await screen.findByRole('radiogroup', { name: 'Role of Ada' });
    fireEvent.click(within(roles).getByRole('radio', { name: 'Viewer' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('at least one active project admin')));
    expect(within(roles).getByRole('radio', { name: 'Project admin' })).toHaveAttribute('aria-checked', 'true');
  });

  it('keeps the remove dialog open and says why when the server refuses the removal', async () => {
    mswServer.use(
      members([ada]),
      http.delete('*/beacon/api/projects/5/members/3', () =>
        conflict("The project's members changed concurrently; reload and retry."),
      ),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Remove Ada' }));
    fireEvent.click(await screen.findByRole('button', { name: /^remove$/i }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('reload and retry')));
    expect(screen.getByRole('button', { name: /^remove$/i })).toBeInTheDocument();
  });

  it('says so when the members cannot be loaded', async () => {
    mswServer.use(http.get('*/beacon/api/projects/5/members', () => new HttpResponse(null, { status: 500 })));

    renderWithProviders(<MembersTab projectId={5} />);

    expect(await screen.findByText('Failed to load members')).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: /project members/i })).not.toBeInTheDocument();
  });

  it('says so when the search fails', async () => {
    mswServer.use(
      members([ada]),
      http.get('*/beacon/api/projects/5/member-candidates', () => new HttpResponse(null, { status: 500 })),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    fireEvent.change(screen.getByRole('searchbox', { name: /search people to add/i }), { target: { value: 'gr' } });

    expect(await screen.findByText(/the search failed/i)).toBeInTheDocument();
  });

  it('names people by display name, or by a neutral label without one, and says the search matches display names', async () => {
    mswServer.use(
      members([ada, { ...ada, userId: 9, displayName: '', role: ProjectRole.Viewer }]),
      http.get('*/beacon/api/projects/5/member-candidates', () =>
        HttpResponse.json({ candidates: [{ userId: 12, displayName: ' ', isMember: false, canAdd: true }] }),
      ),
    );

    renderWithProviders(<MembersTab projectId={5} />);

    expect(screen.getByText(/matches display names only/i)).toBeInTheDocument();
    expect(screen.getByRole('searchbox', { name: /search people to add/i })).toHaveAttribute('placeholder', expect.stringMatching(/display name/i));
    const table = await screen.findByRole('table', { name: /project members/i });
    expect(await within(table).findByText('User 9')).toBeInTheDocument();

    fireEvent.change(screen.getByRole('searchbox', { name: /search people to add/i }), { target: { value: 'us' } });
    expect(await screen.findByRole('button', { name: 'Add User 12' })).toBeInTheDocument();
  });
});
