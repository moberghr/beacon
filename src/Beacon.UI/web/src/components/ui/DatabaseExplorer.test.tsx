import { describe, it, expect, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { DatabaseExplorer } from './DatabaseExplorer';

const sampleSnapshot = {
  dataSourceId: 7,
  databaseEngineType: 'PostgreSQL',
  refreshedAt: '2026-05-08T10:00:00Z',
  tables: [
    {
      schemaName: 'public',
      tableName: 'users',
      description: null,
      indexes: [],
      columns: [
        {
          columnName: 'id',
          dataType: 'integer',
          isNullable: false,
          isPrimaryKey: true,
          isForeignKey: false,
          ordinalPosition: 1,
          foreignKeyTable: null,
          foreignKeyColumn: null,
          defaultValue: null,
          maxLength: null,
          description: null,
        },
        {
          columnName: 'email',
          dataType: 'text',
          isNullable: true,
          isPrimaryKey: false,
          isForeignKey: false,
          ordinalPosition: 2,
          foreignKeyTable: null,
          foreignKeyColumn: null,
          defaultValue: null,
          maxLength: null,
          description: null,
        },
      ],
    },
    {
      schemaName: 'hangfire',
      tableName: 'jobs',
      description: null,
      indexes: [],
      columns: [],
    },
  ],
};

describe('DatabaseExplorer', () => {
  it('renders schema groups and inserts qualified name on table click', async () => {
    mswServer.use(
      http.get('*/beacon/api/data-sources/7/metadata', () =>
        HttpResponse.json(sampleSnapshot),
      ),
    );

    const onInsert = vi.fn();
    renderWithProviders(<DatabaseExplorer dataSourceId={7} onInsert={onInsert} />);

    await waitFor(() => {
      expect(screen.getByText('public')).toBeInTheDocument();
    });
    expect(screen.getByText('hangfire')).toBeInTheDocument();

    // Schemas start collapsed — expand them to reveal table names.
    fireEvent.click(screen.getByText('public'));
    fireEvent.click(screen.getByText('hangfire'));

    expect(screen.getByText('users')).toBeInTheDocument();
    expect(screen.getByText('jobs')).toBeInTheDocument();

    fireEvent.click(screen.getByText('users'));
    expect(onInsert).toHaveBeenCalledWith('public.users');
  });

  it('renders nothing when dataSourceId is null', () => {
    const { container } = renderWithProviders(
      <DatabaseExplorer dataSourceId={null} onInsert={() => {}} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
