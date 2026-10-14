import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import { mswServer } from '../../../vitest.setup';
import ApiKeysListPage from './ApiKeysListPage';

const everyUsersKeys = (canRevoke: boolean) => http.get('*/beacon/api/api-keys/admin', () =>
  HttpResponse.json({
    totalCount: 1,
    pageCount: 1,
    items: [
      {
        id: 2,
        name: 'Reporting',
        prefix: 'sk-sem_repo',
        scopes: ['Read'],
        allowedProjectIds: null,
        createdAt: '2026-06-02T09:00:00Z',
        lastUsedAt: null,
        expiresAt: null,
        revokedAt: null,
        isActive: true,
        userId: 5,
        userName: 'bob',
        canRevoke,
      },
    ],
  }),
);

const ownKeys = http.get('*/beacon/api/api-keys', () => HttpResponse.json({ totalCount: 0, pageCount: 0, items: [] }));

describe('ApiKeysListPage and every user\'s keys', () => {
  it('lists them for a UserAdmin with no Viewer or Editor role, who may revoke them', async () => {
    // onUnhandledRequest is 'error': a request for the caller's own keys would fail this test.
    mswServer.use(functionalOnly('UserAdmin'), everyUsersKeys(true));

    renderWithProviders(<ApiKeysListPage />);

    expect(await screen.findByText('Reporting')).toBeInTheDocument();
    expect(screen.getByText('All API keys')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Revoke' })).toBeInTheDocument();
  });

  it('lists them for an Auditor, who may not revoke them', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Auditor'] }), ownKeys, everyUsersKeys(false));

    renderWithProviders(
      <>
        <ApiKeysListPage />
        <AccessReady />
      </>,
    );

    expect(await screen.findByText('Reporting')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('button', { name: 'Revoke' })).not.toBeInTheDocument();
    expect(screen.queryByText('Admin only')).not.toBeInTheDocument();
  });

  it('does not list them for a holder of every other permission', async () => {
    // onUnhandledRequest is 'error': a request for every user's keys would fail this test.
    mswServer.use(
      permissionsHandler({ permissions: ['Approver', 'SecurityAdmin', 'DataSourceAdmin', 'MigrationOperator'] }),
      ownKeys,
    );

    renderWithProviders(
      <>
        <ApiKeysListPage />
        <AccessReady />
      </>,
    );

    expect(await screen.findByText('No API keys yet')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByText('All API keys')).not.toBeInTheDocument();
  });
});
