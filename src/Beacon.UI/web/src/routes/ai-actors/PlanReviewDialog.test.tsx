import { describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { AiActorActionType, AiActorPlanStatus } from '@/lib/enums';
import { PlanReviewDialog } from './PlanReviewDialog';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function plan(overrides: Record<string, unknown> = {}) {
  return {
    planId: 7,
    actorId: 3,
    actorName: 'Revenue watcher',
    status: AiActorPlanStatus.PendingApproval,
    userInstruction: null,
    analysis: 'Revenue dipped twice this week.',
    findings: ['Two dips below the weekly average.'],
    proposedActions: [
      {
        actionType: AiActorActionType.RefineQuery,
        reasoning: 'Compare with the weekly average.',
        parameters: { queryId: 12 },
        currentSql: 'SELECT 1 FROM orders',
        proposedSql: 'SELECT 2 FROM orders',
        isLocked: false,
        targetQueryName: 'Revenue floor',
        targetQueryId: 12,
      },
      {
        actionType: AiActorActionType.CreateQuery,
        reasoning: 'Watch refunds too.',
        parameters: { name: 'Refunds', sql: 'SELECT count(*) FROM refunds' },
        currentSql: null,
        proposedSql: null,
        isLocked: false,
        targetQueryName: null,
        targetQueryId: null,
      },
    ],
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
    ...overrides,
  };
}

function renderDialog(payload: Record<string, unknown>, onClose = vi.fn()) {
  mswServer.use(http.get('*/beacon/api/ai-actors/plans/7', () => HttpResponse.json(payload)));
  renderWithProviders(<PlanReviewDialog open actorId={3} planId={7} onClose={onClose} />);
  return onClose;
}

describe('PlanReviewDialog', () => {
  it("shows each proposed action with its SQL: a refinement's current and proposed SQL, a new query's SQL", async () => {
    renderDialog(plan());

    expect(await screen.findByText('Revenue dipped twice this week.')).toBeInTheDocument();
    expect(screen.getByText('Two dips below the weekly average.')).toBeInTheDocument();
    expect(screen.getByText('Refine query')).toBeInTheDocument();
    expect(screen.getByText('Current SQL')).toBeInTheDocument();
    expect(screen.getByText('SELECT 1 FROM orders')).toBeInTheDocument();
    expect(screen.getByText('SELECT 2 FROM orders')).toBeInTheDocument();
    expect(screen.getByText('Create query')).toBeInTheDocument();
    expect(screen.getByText('SELECT count(*) FROM refunds')).toBeInTheDocument();
  });

  it('approves with the optional comment and closes', async () => {
    let body: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/ai-actors/plans/7/approve', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ success: true });
      }),
    );
    const onClose = renderDialog(plan());

    fireEvent.change(await screen.findByLabelText('Comment'), { target: { value: 'Looks right' } });
    fireEvent.click(screen.getByRole('button', { name: 'Approve and run' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(body).toEqual({ comment: 'Looks right' });
  });

  it('keeps the dialog open when the server reports the approval failed', async () => {
    mswServer.use(
      http.post('*/beacon/api/ai-actors/plans/7/approve', () =>
        HttpResponse.json({ success: false, errorMessage: 'The plan expired.' }),
      ),
    );
    const onClose = renderDialog(plan());

    fireEvent.click(await screen.findByRole('button', { name: 'Approve and run' }));

    const { toast } = await import('sonner');
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The plan expired.'));
    expect(onClose).not.toHaveBeenCalled();
  });

  it('hides Approve and says why when the server says the caller may not approve', async () => {
    renderDialog(
      plan({
        mayApprove: false,
        approveBlockedReason: "An AI actor's plan is approved by someone other than the actor's creator.",
      }),
    );

    expect(await screen.findByText('You cannot approve this plan')).toBeInTheDocument();
    expect(screen.getByText("An AI actor's plan is approved by someone other than the actor's creator.")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve and run' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Request revision' })).toBeInTheDocument();
  });

  it('offers no decision to a caller who may neither approve nor reject', async () => {
    renderDialog(plan({ mayApprove: false, approveBlockedReason: 'With the approval workflow on, an AI actor\'s plan is approved by an Approver.', mayRejectOrRevise: false }));

    expect(await screen.findByText('You cannot approve this plan')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve and run' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Request revision' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Comment')).not.toBeInTheDocument();
  });

  it('asks for a reason before rejecting, then sends it', async () => {
    const reject = vi.fn();
    mswServer.use(
      http.post('*/beacon/api/ai-actors/plans/7/reject', async ({ request }) => {
        reject(await request.json());
        return HttpResponse.json({ success: true, planId: 7 });
      }),
    );
    const onClose = renderDialog(plan());

    fireEvent.click(await screen.findByRole('button', { name: 'Reject' }));
    expect(await screen.findByText('Say why the plan is rejected.')).toBeInTheDocument();
    expect(reject).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Comment'), { target: { value: 'Not needed' } });
    fireEvent.click(screen.getByRole('button', { name: 'Reject' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(reject).toHaveBeenCalledWith({ reason: 'Not needed' });
  });

  it('sends the feedback when asking for a revision', async () => {
    let body: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/ai-actors/plans/7/request-revision', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json({ success: true, originalPlanId: 7, newPlanId: 8 });
      }),
    );
    const onClose = renderDialog(plan());

    fireEvent.change(await screen.findByLabelText('Comment'), { target: { value: 'Use a 30-day window' } });
    fireEvent.click(screen.getByRole('button', { name: 'Request revision' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(body).toEqual({ feedback: 'Use a 30-day window' });
  });

  it('says an expired plan can no longer be decided', async () => {
    renderDialog(plan({ isExpired: true, mayApprove: false, mayRejectOrRevise: false }));

    expect(await screen.findByText('This plan expired')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve and run' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Reject' })).not.toBeInTheDocument();
  });

  it('lists the earlier versions with the feedback that shaped this one', async () => {
    mswServer.use(
      http.get('*/beacon/api/ai-actors/plans/6', () =>
        HttpResponse.json(
          plan({
            planId: 6,
            version: 1,
            status: AiActorPlanStatus.RevisionRequested,
            reviewedAt: '2026-10-19T10:00:00Z',
            reviewerComment: 'Use the weekly average',
            mayApprove: false,
            mayRejectOrRevise: false,
          }),
        ),
      ),
    );
    renderDialog(plan({ version: 2, parentPlanId: 6 }));

    expect(await screen.findByText('Earlier versions')).toBeInTheDocument();
    expect(await screen.findByText('Revision requested')).toBeInTheDocument();
    expect(screen.getByText('“Use the weekly average”')).toBeInTheDocument();
  });
});
