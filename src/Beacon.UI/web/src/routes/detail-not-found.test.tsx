import { describe, it, expect } from 'vitest';
import { http, HttpResponse } from 'msw';
import type { ComponentType } from 'react';
import { Route, Routes } from 'react-router-dom';
import { screen } from '@testing-library/react';
import { mswServer } from '../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { NOT_FOUND_DETAIL } from '@/lib/api';
import DataSourceDetailPage from './data-sources/DataSourceDetailPage';
import QueryDetailPage from './queries/QueryDetailPage';
import TaskDetailPage from './tasks/TaskDetailPage';
import AiActorDetailPage from './ai-actors/AiActorDetailPage';
import DataContractDetailPage from './data-quality/DataContractDetailPage';

interface DetailCase {
  page: string;
  Component: ComponentType;
  route: string;
  url: string;
  /** The resource's own endpoint. */
  endpoint: string;
  notFound: string;
  failed: string;
}

const CASES: DetailCase[] = [
  { page: 'DataSourceDetailPage', Component: DataSourceDetailPage, route: '/data-sources/:id', url: '/data-sources/42', endpoint: '*/beacon/api/data-sources/42', notFound: 'Data source not found', failed: 'Failed to load data source' },
  { page: 'QueryDetailPage', Component: QueryDetailPage, route: '/queries/:id', url: '/queries/42', endpoint: '*/beacon/api/queries/42', notFound: 'Query not found', failed: 'Failed to load query' },
  { page: 'TaskDetailPage', Component: TaskDetailPage, route: '/tasks/:id', url: '/tasks/42', endpoint: '*/beacon/api/tasks/42', notFound: 'Task not found', failed: 'Failed to load task' },
  { page: 'AiActorDetailPage', Component: AiActorDetailPage, route: '/ai-actors/:id', url: '/ai-actors/42', endpoint: '*/beacon/api/ai-actors/42', notFound: 'AI actor not found', failed: 'Failed to load actor' },
  { page: 'DataContractDetailPage', Component: DataContractDetailPage, route: '/data-quality/:id', url: '/data-quality/42', endpoint: '*/beacon/api/data-quality/contracts/42', notFound: 'Contract not found', failed: 'Failed to load contract' },
];

function answer(endpoint: string, status: number) {
  mswServer.use(
    http.get(endpoint, () =>
      status === 404
        ? HttpResponse.json({ title: 'Not found', status }, { status })
        : HttpResponse.json({ title: 'Server error', status }, { status }),
    ),
    // Whatever else the page asks for (related lists, the signed-in user) finds nothing.
    http.get('*/beacon/api/*', () => HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })),
  );
}

function renderDetail({ Component, route, url }: DetailCase) {
  renderWithProviders(
    <Routes>
      <Route path={route} element={<Component />} />
    </Routes>,
    { initialEntries: [url] },
  );
}

describe('detail pages', () => {
  it.each(CASES)('$page shows the not-found state for a 404: missing, or not readable by the caller', async x => {
    answer(x.endpoint, 404);

    renderDetail(x);

    expect(await screen.findByText(x.notFound)).toBeInTheDocument();
    expect(screen.getByText(NOT_FOUND_DETAIL)).toBeInTheDocument();
    expect(screen.queryByText(x.failed)).not.toBeInTheDocument();
  });

  it.each(CASES)('$page shows a load failure, not the not-found state, for a 500', async x => {
    answer(x.endpoint, 500);

    renderDetail(x);

    expect(await screen.findByText(x.failed)).toBeInTheDocument();
    expect(screen.queryByText(x.notFound)).not.toBeInTheDocument();
    expect(screen.queryByText(NOT_FOUND_DETAIL)).not.toBeInTheDocument();
  });
});
