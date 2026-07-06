import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../vitest.setup';

import ApiKeysListPage from './api-keys/ApiKeysListPage';
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

describe('ApiKeysListPage', () => {
  it('renders keys from /beacon/api/api-keys', async () => {
    mswServer.use(
      http.get('*/beacon/api/api-keys', () =>
        HttpResponse.json({
          entries: [
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
          entries: [
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
