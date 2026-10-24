import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { Route, Routes } from 'react-router-dom';
import { fireEvent, screen } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsHandler } from '@/test/permissions';
import { AccessReady } from '@/test/AccessReady';
import { AiActorExecutionPhase, AiActorPlanStatus, AiActorStatus } from '@/lib/enums';
import AiActorDetailPage from './AiActorDetailPage';

function actor(overrides: Record<string, unknown> = {}) {
  return {
    actorId: 3,
    name: 'Revenue watcher',
    instructions: 'Watch revenue.',
    additionalContext: null,
    dataSourceId: 1,
    dataSourceName: 'ledger-db',
    status: AiActorStatus.Active,
    maxQueries: 3,
    maxSubscriptionsPerQuery: 2,
    requiresApproval: true,
    totalTokensUsed: 100,
    totalCost: 0.01,
    lastThinkTime: null,
    thinkCount: 2,
    lastError: null,
    createdTime: '2026-10-01T10:00:00Z',
    pendingPlanCount: 0,
    hasOwner: false,
    ownerDisplayName: null,
    recentExecutions: [],
    ...overrides,
  };
}

function renderActor(payload: Record<string, unknown>) {
  mswServer.use(
    http.get('*/beacon/api/auth/me', () =>
      HttpResponse.json({ userId: 'u1', displayName: 'Tester', email: null, isAuthenticated: true, roles: [] }),
    ),
    http.get('*/beacon/api/ai-actors/3', () => HttpResponse.json(payload)),
  );
  return renderWithProviders(
    <>
      <Routes>
        <Route path="/ai-actors/:id" element={<AiActorDetailPage />} />
      </Routes>
      <AccessReady />
    </>,
    { initialEntries: ['/ai-actors/3'] },
  );
}

describe('AiActorDetailPage', () => {
  it("names the actor's owner, never by id", async () => {
    renderActor(actor({ hasOwner: true, ownerDisplayName: 'Ada Lovelace' }));
    expect(await screen.findByText('Ada Lovelace')).toBeInTheDocument();
  });

  it('shows a neutral label when no owner is recorded or the owner is no longer a user', async () => {
    renderActor(actor());
    expect(await screen.findByText('No owner recorded')).toBeInTheDocument();
  });

  it('offers a UserAdmin the owner change, and a holder of every other permission none', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    const { unmount } = renderActor(actor());
    expect(await screen.findByRole('button', { name: 'Change owner' })).toBeInTheDocument();
    unmount();

    mswServer.use(
      permissionsHandler({ permissions: ['Approver', 'Auditor', 'SecurityAdmin', 'DataSourceAdmin', 'MigrationOperator'] }),
    );
    renderActor(actor());
    expect(await screen.findByText('No owner recorded')).toBeInTheDocument();
    await screen.findByTestId('access-ready');
    expect(screen.queryByRole('button', { name: 'Change owner' })).not.toBeInTheDocument();
  });

  it('labels a skipped think cycle and says why', async () => {
    renderActor(
      actor({
        hasOwner: true,
        ownerDisplayName: null,
        recentExecutions: [
          {
            executionId: 9,
            phase: AiActorExecutionPhase.Skipped,
            startedAt: '2026-10-10T07:00:00Z',
            completedAt: '2026-10-10T07:00:00Z',
            queriesCreated: 0,
            subscriptionsCreated: 0,
            tokensUsed: 0,
            estimatedCost: 0,
            decisionSummary: null,
            errorMessage: 'Skipped: owner not authorized.',
          },
          {
            executionId: 8,
            phase: AiActorExecutionPhase.Completed,
            startedAt: '2026-10-09T07:00:00Z',
            completedAt: '2026-10-09T07:01:00Z',
            queriesCreated: 1,
            subscriptionsCreated: 0,
            tokensUsed: 50,
            estimatedCost: 0.01,
            decisionSummary: 'Added a revenue drop watch.',
            errorMessage: null,
          },
        ],
      }),
    );

    expect(await screen.findByText('Skipped')).toBeInTheDocument();
    expect(screen.getByText('Skipped: owner not authorized.')).toBeInTheDocument();
    expect(screen.getByText('Completed')).toBeInTheDocument();
    expect(screen.getByText('Added a revenue drop watch.')).toBeInTheDocument();
    expect(screen.getByText('Unknown user')).toBeInTheDocument();
  });

  it('lists the plans waiting for approval and opens one for review', async () => {
    mswServer.use(
      http.get('*/beacon/api/ai-actors/3/pending-plans', () =>
        HttpResponse.json({
          plans: [
            {
              planId: 7,
              actorId: 3,
              actorName: 'Revenue watcher',
              userInstruction: null,
              analysis: 'Revenue dipped twice this week.',
              actionCount: 2,
              proposedAt: '2026-10-20T10:00:00Z',
              expiresAt: '2026-10-27T10:00:00Z',
              version: 1,
              tokensUsed: 120,
              estimatedCost: 0.01,
            },
          ],
        }),
      ),
      http.get('*/beacon/api/ai-actors/plans/7', () =>
        HttpResponse.json({
          planId: 7,
          actorId: 3,
          actorName: 'Revenue watcher',
          status: AiActorPlanStatus.PendingApproval,
          userInstruction: null,
          analysis: 'Revenue dipped twice this week.',
          findings: [],
          proposedActions: [],
          proposedAt: '2026-10-20T10:00:00Z',
          expiresAt: '2026-10-27T10:00:00Z',
          isExpired: false,
          reviewedAt: null,
          reviewerComment: null,
          executedAt: null,
          executionId: null,
          tokensUsed: 120,
          estimatedCost: 0.01,
          model: null,
          version: 1,
          parentPlanId: null,
          mayApprove: true,
          approveBlockedReason: null,
          mayRejectOrRevise: true,
        }),
      ),
    );
    renderActor(actor({ pendingPlanCount: 1 }));

    expect(await screen.findByText('Plans waiting for approval')).toBeInTheDocument();
    expect(await screen.findByText('Revenue dipped twice this week.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Review' }));

    expect(await screen.findByRole('button', { name: 'Approve and run' })).toBeInTheDocument();
    expect(screen.getByText('The plan proposes no actions.')).toBeInTheDocument();
  });

  it('says when no plan waits, and shows no plan panel for an actor that needs no approval', async () => {
    const { unmount } = renderActor(actor());
    expect(await screen.findByText('No plan waits for a decision.')).toBeInTheDocument();
    unmount();

    renderActor(actor({ requiresApproval: false }));
    expect(await screen.findByText('No owner recorded')).toBeInTheDocument();
    expect(screen.queryByText('Plans waiting for approval')).not.toBeInTheDocument();
  });
});
