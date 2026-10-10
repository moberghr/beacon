import { cleanup, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../vitest.setup';

import ApiKeysListPage from './api-keys/ApiKeysListPage';
import { GenerateApiKeyDialog } from './api-keys/GenerateApiKeyDialog';
import UsersListPage from './users/UsersListPage';
import AdminSettingsPage from './admin-settings/AdminSettingsPage';
import SettingsPage from './settings/SettingsPage';
import DataQualityPage from './data-quality/DataQualityPage';

/**
 * Smoke tests for the five admin / settings / data-quality pages that previously
 * had no coverage and were the MSW mock-mode gaps. Each test registers exactly the
 * endpoints the page hits on mount (the global setup uses onUnhandledRequest:'error',
 * so an unexpected request fails the test) and asserts the page mounts and renders
 * content driven by that response — i.e. the page ↔ endpoint contract holds.
 */

const adminMe = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({
    userId: 'mock-admin',
    displayName: 'Mock Admin',
    email: 'mock.admin@example.test',
    isAuthenticated: true,
    roles: ['Admin'],
  }),
);

const editorMe = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({
    userId: 'mock-editor',
    displayName: 'Mock Editor',
    email: 'mock.editor@example.test',
    isAuthenticated: true,
    roles: ['Editor'],
  }),
);

const ownKeys = http.get('*/beacon/api/api-keys', () =>
  HttpResponse.json({
    totalCount: 1,
    pageCount: 1,
    items: [
      {
        id: 1,
        name: 'Demo CI Key',
        prefix: 'sk-sem_demo',
        scopes: ['Read', 'Execute'],
        allowedProjectIds: [4],
        createdAt: '2026-06-01T09:00:00Z',
        lastUsedAt: null,
        expiresAt: '2026-08-30T09:00:00Z',
        isActive: true,
      },
    ],
  }),
);

describe('ApiKeysListPage', () => {
  it('shows a non-admin only their own keys', async () => {
    // onUnhandledRequest is 'error': a request for every user's keys would fail this test.
    mswServer.use(editorMe, ownKeys);

    renderWithProviders(<ApiKeysListPage />);

    await waitFor(() => expect(screen.getByText('Demo CI Key')).toBeInTheDocument());
    expect(screen.getByText('#4')).toBeInTheDocument();
    expect(screen.queryByText('All API keys')).not.toBeInTheDocument();
  });

  it('shows an admin every user\'s keys with their owner', async () => {
    mswServer.use(
      adminMe,
      ownKeys,
      http.get('*/beacon/api/api-keys/admin', () =>
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
              expiresAt: '2026-08-31T09:00:00Z',
              revokedAt: null,
              isActive: true,
              userId: 5,
              userName: 'bob',
            },
          ],
        }),
      ),
    );

    renderWithProviders(<ApiKeysListPage />);

    await waitFor(() => expect(screen.getByText('All API keys')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('bob')).toBeInTheDocument());
    expect(screen.getByText('Reporting')).toBeInTheDocument();
  });

  it('renders keys from /beacon/api/api-keys', async () => {
    mswServer.use(
      editorMe,
      http.get('*/beacon/api/api-keys', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [
            {
              id: 1,
              name: 'Demo CI Key',
              prefix: 'sk-sem_demo',
              scopes: ['Read', 'Execute'],
              createdAt: '2026-06-01T09:00:00Z',
              lastUsedAt: null,
              expiresAt: null,
              isActive: true,
            },
          ],
        }),
      ),
    );

    renderWithProviders(<ApiKeysListPage />);

    await waitFor(() => expect(screen.getByText('Demo CI Key')).toBeInTheDocument());
  });
});

describe('UsersListPage', () => {
  it('renders users from /beacon/api/users', async () => {
    mswServer.use(
      http.get('*/beacon/api/users', () =>
        HttpResponse.json({
          totalCount: 1,
          pageCount: 1,
          items: [
            {
              id: 1,
              userName: 'mock.admin',
              email: 'mock.admin@example.test',
              displayName: 'Mock Admin',
              isInternalUser: true,
              isSuperAdmin: true,
              isEnabled: true,
              lastLoginAt: '2026-06-25T08:00:00Z',
              roles: [{ id: 1, name: 'Admin', level: 100 }],
            },
          ],
        }),
      ),
    );

    renderWithProviders(<UsersListPage />);

    await waitFor(() => expect(screen.getByText('mock.admin')).toBeInTheDocument());
  });
});

describe('AdminSettingsPage', () => {
  it('renders provider settings from /beacon/api/admin-settings for an admin', async () => {
    mswServer.use(
      adminMe,
      http.get('*/beacon/api/admin-settings', () =>
        HttpResponse.json({
          settings: {
            baseUrl: 'https://demo.beacon.test',
            llmProvider: null,
            llmApiKeySet: false,
            llmEndpointSet: false,
            llmRegion: null,
            llmSessionTokenSet: false,
            llmAwsAccessKeyIdSet: false,
            llmAwsSecretAccessKeySet: false,
            llmBedrockAuthMode: 0,
            llmModel: null,
            llmFastModel: null,
            llmMaxConcurrentRequests: 4,
            llmTokensPerMinute: 100000,
            llmRequestsPerMinute: 60,
            llmMonthlyBudget: 0,
          },
          history: [],
        }),
      ),
    );

    renderWithProviders(<AdminSettingsPage />);

    await waitFor(() => expect(screen.getByText('General')).toBeInTheDocument());
  });
});

describe('SettingsPage', () => {
  it('renders account info from /beacon/api/user-settings', async () => {
    mswServer.use(
      http.get('*/beacon/api/user-settings', () =>
        HttpResponse.json({
          user: {
            userName: 'mock.admin',
            email: 'mock.admin@example.test',
            displayName: 'Mock Admin',
            isInternalUser: true,
            roles: ['Admin'],
          },
        }),
      ),
    );

    renderWithProviders(<SettingsPage />);

    await waitFor(() => expect(screen.getByDisplayValue('mock.admin')).toBeInTheDocument());
  });
});

describe('DataQualityPage', () => {
  it('renders empty states from /beacon/api/data-quality/{overview,contracts}', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-quality/overview', () => HttpResponse.json([])),
      http.get('*/beacon/api/data-quality/contracts', () => HttpResponse.json([])),
    );

    renderWithProviders(<DataQualityPage />);

    await waitFor(() => expect(screen.getByText('No contracts yet')).toBeInTheDocument());
  });
});

describe('GenerateApiKeyDialog', () => {
  // Pages rendered by earlier tests stay mounted (and their dialogs portal into document.body): start each test clean.
  beforeEach(cleanup);

  // The scope checkboxes sit inside the Scopes field's label too, so their accessible names overlap: find each by
  // its own label's text.
  const scopeCheckbox = (text: RegExp) =>
    screen.getByText(text).closest('label')!.querySelector('input') as HTMLInputElement;

  it('offers Read and Execute only, and Execute only to writers', async () => {
    mswServer.use(
      http.get('*/beacon/api/auth/permissions', () => HttpResponse.json({ canRead: true, canWrite: false })),
    );

    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);

    // Execute is disabled while the permissions load too: wait until they have loaded.
    expect(await screen.findByText(/requires the Editor role/)).toBeInTheDocument();
    expect(scopeCheckbox(/^Execute$/)).toBeDisabled();
    expect(scopeCheckbox(/^Read$/)).toBeEnabled();
    expect(screen.getAllByRole('checkbox')).toHaveLength(2);
    expect(screen.queryByText(/Admin/)).not.toBeInTheDocument();
  });

  it('lets a writer pick Execute', async () => {
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);

    await waitFor(() => expect(scopeCheckbox(/^Execute$/)).toBeEnabled());
  });
});
