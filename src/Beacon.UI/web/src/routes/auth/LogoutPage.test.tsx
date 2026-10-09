import { fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { mswServer } from '../../../vitest.setup';
import LogoutPage from './LogoutPage';

function countLogouts() {
  const calls = { count: 0 };
  mswServer.use(
    http.get('*/beacon/api/csrf', () => new HttpResponse(null, { status: 204 })),
    http.post('*/beacon/api/auth/logout', () => {
      calls.count += 1;
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return calls;
}

function renderLogout(state?: unknown) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[{ pathname: '/logout', state }]}>
        <Routes>
          <Route path="/logout" element={<LogoutPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('LogoutPage', () => {
  it('asks for a click when reached from outside the app, and signs out only then', async () => {
    const calls = countLogouts();

    renderLogout();

    const confirm = await screen.findByRole('button', { name: /sign out/i });
    expect(calls.count).toBe(0);

    fireEvent.click(confirm);

    await waitFor(() => expect(calls.count).toBe(1));
    expect(await screen.findByText(/session has been cleared/i)).toBeInTheDocument();
  });

  it('signs out at once when the app itself sent the user here', async () => {
    const calls = countLogouts();

    renderLogout({ fromApp: true });

    await waitFor(() => expect(calls.count).toBe(1));
    expect(screen.queryByRole('button', { name: /sign out/i })).toBeNull();
  });
});
