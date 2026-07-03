import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../../vitest.setup';
import LoginPage from './LoginPage';

const anonymousMe = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({
    userId: null,
    displayName: null,
    email: null,
    isAuthenticated: false,
    roles: [],
  }),
);

const SSO_LABEL = /continue with single sign-on/i;

describe('LoginPage SSO button', () => {
  it('hides the SSO button when SSO is not configured', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: false })),
    );

    renderWithProviders(<LoginPage />);

    // The username field is always present — once it renders, the SSO query has settled.
    await waitFor(() => expect(screen.getByPlaceholderText('you@moberg.hr')).toBeInTheDocument());
    expect(screen.queryByText(SSO_LABEL)).not.toBeInTheDocument();
  });

  it('shows the SSO button when SSO is configured', async () => {
    mswServer.use(
      anonymousMe,
      http.get('*/beacon/api/auth/sso', () => HttpResponse.json({ enabled: true })),
    );

    renderWithProviders(<LoginPage />);

    const ssoLink = await screen.findByText(SSO_LABEL);
    expect(ssoLink.closest('a')).toHaveAttribute('href', '/beacon/api/auth/sso/challenge');
  });
});
