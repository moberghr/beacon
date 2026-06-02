import { screen, waitFor } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import ProjectsListPage from './ProjectsListPage';
import { renderWithProviders } from '@/test/render';

describe('ProjectsListPage', () => {
  it('renders rows from /beacon/api/projects', async () => {
    renderWithProviders(<ProjectsListPage />);

    await waitFor(() => {
      expect(screen.getByText('Acme Analytics')).toBeInTheDocument();
      expect(screen.getByText('Beta Pipeline')).toBeInTheDocument();
    });

    // Header row + 2 data rows = 2 in the count line ("2 total")
    expect(screen.getByText(/2 total/)).toBeInTheDocument();
  });
});
