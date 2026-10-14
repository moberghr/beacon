import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import { OwnedResources } from './OwnedResources';

/**
 * A user's owned resources in the user dialog, for offboarding: one kind per tab (the server's numeric kind), paged,
 * read only once shown, for an Auditor or a UserAdmin; nothing for anyone else. A resource the caller may not read is
 * listed by id and kind, its name withheld.
 */

function item(id: number, overrides: Record<string, unknown> = {}) {
  return {
    id,
    kind: 0,
    name: `Revenue check ${id}`,
    isOwner: true,
    isCreator: false,
    isLastModifier: false,
    nameWithheld: false,
    createdTime: '2026-09-01T10:00:00Z',
    modifiedTime: null,
    ...overrides,
  };
}

function serve(requests: URL[], reply: (url: URL) => Response) {
  mswServer.use(
    http.get('*/beacon/api/users/7/owned-resources', ({ request }) => {
      const url = new URL(request.url);
      requests.push(url);
      return reply(url);
    }),
  );
}

describe('OwnedResources', () => {
  it('shows nothing to a user holding neither Auditor nor UserAdmin', async () => {
    let answered = false;
    mswServer.use(
      http.get('*/beacon/api/auth/permissions', () => {
        answered = true;
        return HttpResponse.json({ canRead: true, canWrite: true, isAdmin: false, permissions: ['SecurityAdmin'], migrationsEnabled: true });
      }),
    );

    renderWithProviders(<OwnedResources userId={7} />);

    // The permissions have loaded (a SecurityAdmin), and nothing is offered.
    await waitFor(() => expect(answered).toBe(true));
    await waitFor(() => expect(screen.queryByText('Owned resources')).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Show owned resources' })).not.toBeInTheDocument();
  });

  it('reads nothing until shown, then lists the queries with how the user is attributed', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    const requests: URL[] = [];
    serve(requests, () =>
      HttpResponse.json({
        items: [item(3), item(4, { isOwner: false, isCreator: true, isLastModifier: true, modifiedTime: '2026-10-01T10:00:00Z' })],
        totalCount: 2,
        pageCount: 1,
      }));

    renderWithProviders(<OwnedResources userId={7} />);
    const show = await screen.findByRole('button', { name: 'Show owned resources' });
    expect(requests).toHaveLength(0);

    fireEvent.click(show);

    const list = within(await screen.findByRole('list', { name: 'Resources' }));
    expect(list.getByText('Revenue check 3')).toBeInTheDocument();
    expect(list.getAllByText('Owner')).toHaveLength(1);
    expect(list.getByText('Creator')).toBeInTheDocument();
    expect(list.getByText('Last change')).toBeInTheDocument();
    expect(requests[0].searchParams.get('Kind')).toBe('0');
    expect(requests[0].searchParams.get('Page')).toBe('0');
    expect(requests[0].searchParams.get('PageSize')).toBe('10');
  });

  it('reads one kind per tab by its number, and pages within it', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Auditor'] }));
    const requests: URL[] = [];
    serve(requests, url => {
      const page = Number(url.searchParams.get('Page'));
      return url.searchParams.get('Kind') === '2'
        ? HttpResponse.json({ items: [item(20 + page, { kind: 2, name: `Contract ${20 + page}` })], totalCount: 12, pageCount: 2 })
        : HttpResponse.json({ items: [], totalCount: 0, pageCount: 0 });
    });
    renderWithProviders(<OwnedResources userId={7} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Show owned resources' }));
    expect(await screen.findByText('None.')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Data contracts' }));
    expect(await screen.findByText('Contract 20')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));

    expect(await screen.findByText('Contract 21')).toBeInTheDocument();
    expect(requests.at(-1)!.searchParams.get('Kind')).toBe('2');
    expect(requests.at(-1)!.searchParams.get('Page')).toBe('1');

    fireEvent.click(screen.getByRole('tab', { name: 'Migration jobs' }));
    await waitFor(() => expect(requests.at(-1)!.searchParams.get('Kind')).toBe('4'));
    expect(requests.at(-1)!.searchParams.get('Page')).toBe('0');
  });

  it('lists a resource of a project the caller cannot read by id and kind, its name withheld', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve([], () =>
      HttpResponse.json({
        items: [item(3), item(4, { name: '', nameWithheld: true })],
        totalCount: 2,
        pageCount: 1,
      }));
    renderWithProviders(<OwnedResources userId={7} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Show owned resources' }));

    const list = within(await screen.findByRole('list', { name: 'Resources' }));
    const [readable, withheld] = list.getAllByRole('listitem');
    expect(within(readable).getByText('Revenue check 3')).toBeInTheDocument();
    expect(within(readable).queryByText('Name withheld')).not.toBeInTheDocument();
    expect(within(withheld).getByText('#4')).toBeInTheDocument();
    expect(within(withheld).getByText('Name withheld')).toHaveAttribute('title', expect.stringContaining('cannot read'));
    expect(within(withheld).getByText('Owner')).toBeInTheDocument();
  });

  it('says so when the server refuses', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    serve([], () => HttpResponse.json({ title: 'Forbidden' }, { status: 403 }));
    renderWithProviders(<OwnedResources userId={7} />);

    fireEvent.click(await screen.findByRole('button', { name: 'Show owned resources' }));

    expect(await screen.findByText(/Forbidden|could not be loaded|permission/i)).toBeInTheDocument();
  });
});
