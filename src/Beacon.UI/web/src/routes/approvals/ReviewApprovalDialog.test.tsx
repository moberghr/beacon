import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { ReviewApprovalDialog } from './ReviewApprovalDialog';

const DETAIL = {
  id: 7,
  queryId: 3,
  queryName: 'Open invoices',
  queryVersionId: 11,
  status: 1,
  requestedByUserId: 'u1',
  requestedByUserName: 'Ana',
  reviewedByUserName: null,
  reviewedAt: null,
  reviewComment: null,
  changeSummary: 'Tightened the WHERE clause',
  createdTime: '2026-06-01T10:00:00Z',
  proposedVersion: {
    id: 11,
    versionNumber: 2,
    label: null,
    status: 3,
    name: 'Open invoices',
    description: null,
    finalQuery: 'SELECT * FROM invoices WHERE open = true',
    createdTime: '2026-06-01T10:00:00Z',
    createdByUserId: 'u1',
    changeSource: null,
    changeReason: null,
  },
  currentActiveVersion: null,
  autoDiff: null,
};

function mockDetail() {
  mswServer.use(
    http.get('*/beacon/api/approvals/7', () => HttpResponse.json(DETAIL)),
  );
}

describe('ReviewApprovalDialog', () => {
  it('rejects through form validation and posts the typed comment', async () => {
    mockDetail();
    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/approvals/7/reject', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({});
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<ReviewApprovalDialog open approvalId={7} onClose={onClose} />);

    const commentBox = await screen.findByLabelText(/reviewer comment/i);
    fireEvent.input(commentBox, { target: { value: 'Not safe for prod' } });
    fireEvent.click(screen.getByRole('button', { name: /^reject$/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });
    expect(captured).toMatchObject({ comment: 'Not safe for prod' });
  });

  it('blocks reject when the comment exceeds 2000 characters', async () => {
    mockDetail();
    const rejectSpy = vi.fn();
    mswServer.use(
      http.post('*/beacon/api/approvals/7/reject', () => {
        rejectSpy();
        return HttpResponse.json({});
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<ReviewApprovalDialog open approvalId={7} onClose={onClose} />);

    const commentBox = await screen.findByLabelText(/reviewer comment/i);
    fireEvent.input(commentBox, { target: { value: 'x'.repeat(2001) } });
    fireEvent.click(screen.getByRole('button', { name: /^reject$/i }));

    await waitFor(() => {
      expect(screen.getByText(/max 2000 characters/i)).toBeInTheDocument();
    });
    expect(rejectSpy).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('approves with a validated comment', async () => {
    mockDetail();
    let captured: unknown = null;
    mswServer.use(
      http.post('*/beacon/api/approvals/7/approve', async ({ request }) => {
        captured = await request.json();
        return HttpResponse.json({});
      }),
    );

    const onClose = vi.fn();
    renderWithProviders(<ReviewApprovalDialog open approvalId={7} onClose={onClose} />);

    const commentBox = await screen.findByLabelText(/reviewer comment/i);
    fireEvent.input(commentBox, { target: { value: 'LGTM' } });
    fireEvent.click(screen.getByRole('button', { name: /^approve$/i }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });
    expect(captured).toMatchObject({ comment: 'LGTM' });
  });
});
