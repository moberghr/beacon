import { type ReactElement } from 'react';
import { render, type RenderOptions } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';

interface Options extends Omit<RenderOptions, 'wrapper'> {
  initialEntries?: string[];
}

/**
 * Render a component inside the providers it needs in production
 * (TanStack Query + react-router). Each call gets a fresh QueryClient
 * so cache state never leaks between tests.
 */
export function renderWithProviders(ui: ReactElement, options: Options = {}) {
  const { initialEntries = ['/'], ...rest } = options;
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: 0, gcTime: 0 },
    },
  });

  return render(ui, {
    ...rest,
    wrapper: ({ children }) => (
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>
      </QueryClientProvider>
    ),
  });
}
