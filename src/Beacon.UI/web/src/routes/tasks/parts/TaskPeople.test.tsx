import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { mswServer } from '../../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { TaskPriority } from '@/lib/enums';
import type { TaskDetail } from '../queries';
import { RightRail } from './RightRail';
import { TaskInfoCard } from './TaskInfoCard';

function task(overrides: Partial<TaskDetail> = {}): TaskDetail {
  return {
    id: 42,
    queryId: 9,
    queryName: 'orders-overdue',
    subscriptionId: 7,
    subscriptionName: 'orders watch',
    subscriptionDescription: null,
    latestResultCount: 5,
    notificationCount: 1,
    lastNotificationAt: null,
    createdAt: '2026-10-10T08:00:00Z',
    resolved: false,
    resolvedAt: null,
    resolvedByUserName: null,
    resolutionNotes: null,
    aiActorId: null,
    aiActorName: null,
    lastExecutionAt: null,
    cronExpression: null,
    priority: TaskPriority.Normal,
    hasAssignee: false,
    assigneeUserName: null,
    assignedToCaller: false,
    snoozedUntil: null,
    slaHours: null,
    watcherCount: 0,
    isWatching: false,
    hasOwner: false,
    ownerDisplayName: null,
    ...overrides,
  };
}

function signedIn(roles: string[]) {
  return http.get('*/beacon/api/auth/me', () =>
    HttpResponse.json({ userId: 'ext-me', displayName: 'Me', email: null, isAuthenticated: true, roles }),
  );
}

function renderPeople(detail: TaskDetail) {
  return renderWithProviders(
    <>
      <TaskInfoCard task={detail} />
      <RightRail task={detail} relatedResolvedCount={0} />
    </>,
  );
}

const peopleCard = () => screen.getByText('People').closest('div')!.parentElement!;

describe('task people', () => {
  it('lets anyone claim an unassigned task and suggests claiming it', async () => {
    let assigned: unknown = null;
    mswServer.use(
      signedIn([]),
      http.post('*/beacon/api/tasks/42/assign', async ({ request }) => {
        assigned = await request.json();
        return new HttpResponse(null, { status: 204 });
      }),
    );

    renderPeople(task());

    fireEvent.click(await screen.findByRole('button', { name: 'claim' }));
    await waitFor(() => expect(assigned).toEqual({ assigneeUserId: 'ext-me' }));
    expect(screen.getByText('Claim ownership')).toBeInTheDocument();
    expect(within(peopleCard()).getByText('Unassigned')).toBeInTheDocument();
  });

  it('names an assignee and an owner, and offers no claim of an assigned task to a non-Admin', async () => {
    mswServer.use(signedIn([]));

    renderPeople(task({ hasAssignee: true, assigneeUserName: 'Ana Analyst', hasOwner: true, ownerDisplayName: 'Ole Owner' }));

    expect(await screen.findAllByText('Ana Analyst')).toHaveLength(2);
    expect(within(peopleCard()).getByText('Ole Owner')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'claim' })).not.toBeInTheDocument();
    expect(screen.queryByText('Claim ownership')).not.toBeInTheDocument();
  });

  it('shows a neutral label for an assignee or owner who is not a Beacon user', async () => {
    mswServer.use(signedIn(['Admin']));

    renderPeople(task({ hasAssignee: true, assigneeUserName: null, hasOwner: true, ownerDisplayName: null }));

    expect(await screen.findByText('unknown user')).toBeInTheDocument();
    expect(within(peopleCard()).getAllByText('Unknown user')).toHaveLength(2);
    // Assigned to someone else: no claim suggestion, and not "Unassigned".
    expect(screen.queryByText('Claim ownership')).not.toBeInTheDocument();
    expect(within(peopleCard()).queryByText('Unassigned')).not.toBeInTheDocument();
  });

  it('says when no owner is recorded', async () => {
    mswServer.use(signedIn([]));

    renderPeople(task());

    await screen.findByText('People');
    expect(within(peopleCard()).getByText('no owner recorded')).toBeInTheDocument();
  });
});
