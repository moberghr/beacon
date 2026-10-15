import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { Route, Routes } from 'react-router-dom';
import { toast } from 'sonner';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsHandler } from '@/test/permissions';
import { mswServer } from '../../../vitest.setup';
import SecurityAuditPage, { actorOf, detailsOf, verdictsOf } from './SecurityAuditPage';
import type { SecurityAuditEvent, VerifySecurityAuditResult } from './queries';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/**
 * The Auditor's security audit page: a paged read of the trail over a window of at most 93 days, filtered by category,
 * action, actor, resource and project; each event's details with withheld values shown as withheld; and a verification
 * of the shown events, or of one, against their integrity signatures, each row marked with the verdict the server gave
 * it (only an explicit `intact` is shown intact; an unknown key and an event not found apart from a modified one), a
 * verification that checked nothing shown as no success, and one refused while another runs shown as busy. Nobody else
 * gets the page (the server refuses them too); an invalid window reads nothing.
 */

const SUBSCRIPTION_UPDATED: SecurityAuditEvent = {
  id: 12,
  eventGuid: '6f1c1e2a-9d7e-4b8e-9a51-0d2b7c0e4a11',
  createdTime: '2026-10-10T08:00:00.1234567Z',
  category: 'delivery',
  action: 'delivery.subscription_updated',
  outcome: 'success',
  actorKind: 'user',
  actorUserId: 3,
  apiKeyId: null,
  callerHash: null,
  projectId: 2,
  resourceType: 'subscription',
  resourceId: '8',
  detailsJson: '{"changedFields":["schedule","sla"],"recipientIds":[4,5],"reason":"[withheld]","secretChanged":false}',
  traceId: '4bf92f3577b34da6a3ce929d0e0e4736',
  keyId: 'k1-0123456789abcdef',
};

const SIGN_IN_FAILED: SecurityAuditEvent = {
  ...SUBSCRIPTION_UPDATED,
  id: 11,
  eventGuid: '0e7d7a52-61a8-4f4f-8f3e-6d7c6b1e2f00',
  createdTime: '2026-10-10T07:00:00Z',
  category: 'auth',
  action: 'auth.password_sign_in_failed',
  outcome: 'failure',
  actorKind: 'anonymous',
  actorUserId: null,
  projectId: null,
  resourceType: null,
  resourceId: null,
  detailsJson: null,
};

const WINDOW = '/security-audit?from=2026-10-01&to=2026-10-14';
const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ ok: true }));

function serveEvents(items: SecurityAuditEvent[], requests: URL[] = []) {
  mswServer.use(
    http.get('*/beacon/api/security-audit', ({ request }) => {
      requests.push(new URL(request.url));
      return HttpResponse.json({ items, totalCount: items.length, pageCount: items.length === 0 ? 0 : 1 });
    }),
  );
  return requests;
}

function verification(overrides: Partial<VerifySecurityAuditResult>): VerifySecurityAuditResult {
  return {
    checkedCount: 0,
    intactCount: 0,
    modifiedCount: 0,
    unknownKeyCount: 0,
    outOfRangeCount: 0,
    outcome: 'success',
    failedEventIds: [],
    verdicts: null,
    ...overrides,
  };
}

function answerVerification(result: VerifySecurityAuditResult | (() => Response), bodies: unknown[] = []) {
  mswServer.use(
    http.post('*/beacon/api/security-audit/verify', async ({ request }) => {
      bodies.push(await request.json());
      return typeof result === 'function' ? result() : HttpResponse.json(result);
    }),
  );
  return bodies;
}

function renderAt(entry = WINDOW) {
  return renderWithProviders(
    <Routes>
      <Route path="/security-audit" element={<SecurityAuditPage />} />
      <Route path="/home" element={<span>home page</span>} />
    </Routes>,
    { initialEntries: [entry] },
  );
}

describe('SecurityAuditPage', () => {
  beforeEach(() => {
    vi.mocked(toast.error).mockClear();
    mswServer.use(permissionsHandler({ permissions: ['Auditor'] }), csrf);
  });

  it('lists the window\'s events newest first for an Auditor, reading the window as whole UTC days', async () => {
    const requests = serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);

    renderAt();

    expect(await screen.findByText('subscription updated')).toBeInTheDocument();
    const table = within(screen.getByRole('table', { name: 'Security audit events' }));
    const rows = table.getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('delivery.subscription_updated')).toBeInTheDocument();
    expect(within(rows[0]).getByText('User · user #3')).toBeInTheDocument();
    expect(within(rows[0]).getByText('subscription #8')).toBeInTheDocument();
    expect(within(rows[0]).getByText('#2')).toBeInTheDocument();
    expect(within(rows[1]).getByText('password sign in failed')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Anonymous')).toBeInTheDocument();
    expect(within(rows[1]).getByText('failure')).toBeInTheDocument();
    expect(table.getAllByText('Not verified')).toHaveLength(2);
    const params = requests[0].searchParams;
    expect(params.get('from')).toBe('2026-10-01T00:00:00.000Z');
    expect(params.get('to')).toBe('2026-10-14T23:59:59.999Z');
    expect(params.get('sort')).toBe('-createdTime');
    expect(params.has('category')).toBe(false);
  });

  it('opens for an Auditor whose roles are functional only', async () => {
    mswServer.use(functionalOnly('Auditor'));
    serveEvents([SUBSCRIPTION_UPDATED]);

    renderAt();

    expect(await screen.findByText('subscription updated')).toBeInTheDocument();
  });

  it('sends a holder of every other permission home, saying which one the page needs', async () => {
    // onUnhandledRequest is 'error': reading the trail would fail this test.
    mswServer.use(permissionsHandler({ permissions: ['Approver', 'SecurityAdmin', 'UserAdmin', 'DataSourceAdmin', 'MigrationOperator'] }));

    renderAt();

    expect(await screen.findByText('home page')).toBeInTheDocument();
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('The Auditor permission is required for this page.'));
  });

  it('reads nothing while the window is longer than 93 days, and says why', async () => {
    // No handler for the list: a read would fail the test.
    renderAt('/security-audit?from=2026-01-01&to=2026-06-01');

    expect(await screen.findByText('The window cannot be read')).toBeInTheDocument();
    expect(screen.getByText('The window must not exceed 93 days.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Verify shown events/ })).toBeDisabled();
  });

  it('filters by category, action of that category, actor, resource and project', async () => {
    const requests = serveEvents([SUBSCRIPTION_UPDATED]);
    renderAt();
    await screen.findByText('subscription updated');

    fireEvent.change(screen.getByLabelText('Category'), { target: { value: 'delivery' } });
    await waitFor(() => expect(requests.at(-1)!.searchParams.get('category')).toBe('delivery'));
    const actions = within(screen.getByLabelText('Action')).getAllByRole('option').map(x => x.getAttribute('value'));
    expect(actions).toContain('delivery.subscription_updated');
    expect(actions).not.toContain('auth.signed_out');

    // One filter at a time, as a person sets them: each applies on top of the ones before.
    const steps: [string, string, string][] = [
      ['Action', 'action', 'delivery.subscription_updated'],
      ['Resource type', 'resourceType', 'subscription'],
      ['Actor user id', 'actorUserId', '3'],
      ['Resource id', 'resourceId', '8'],
      ['Project id', 'projectId', '2'],
    ];
    for (const [label, param, value] of steps) {
      fireEvent.change(screen.getByLabelText(label), { target: { value } });
      await waitFor(() => expect(requests.at(-1)!.searchParams.get(param)).toBe(value));
    }

    const params = requests.at(-1)!.searchParams;
    expect(params.get('category')).toBe('delivery');
    expect(params.get('action')).toBe('delivery.subscription_updated');
    expect(params.get('resourceType')).toBe('subscription');
    expect(params.get('actorUserId')).toBe('3');
    expect(params.get('resourceId')).toBe('8');
  });

  it('does not apply an action of another category than the chosen one', async () => {
    const requests = serveEvents([]);
    renderAt(`${WINDOW}&category=auth&action=delivery.subscription_updated`);

    await screen.findByText('No events');

    expect(requests.at(-1)!.searchParams.get('category')).toBe('auth');
    expect(requests.at(-1)!.searchParams.has('action')).toBe(false);
    expect(screen.getByLabelText('Action')).toHaveValue('');
  });

  it('offers the settings, approvals, tasks and curation categories and their actions', async () => {
    serveEvents([]);
    renderAt();

    const categories = within(await screen.findByLabelText('Category')).getAllByRole('option').map(x => x.getAttribute('value'));
    expect(categories).toEqual(expect.arrayContaining(['settings', 'approvals', 'tasks', 'curation', 'delivery']));
    const actions = within(screen.getByLabelText('Action')).getAllByRole('option').map(x => x.getAttribute('value'));
    expect(actions).toEqual(expect.arrayContaining([
      'settings.mcp_settings_changed',
      'approvals.ai_plan_approved',
      'tasks.task_auto_resolved',
      'curation.golden_case_promoted',
      'delivery.subscription_updated',
    ]));
    expect(await screen.findByText('No events')).toBeInTheDocument();
  });

  it('shows an event\'s details, a withheld value as withheld', async () => {
    serveEvents([SUBSCRIPTION_UPDATED]);
    renderAt();

    fireEvent.click(await screen.findByRole('button', { name: 'Details of event 12' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Event #12')).toBeInTheDocument();
    expect(within(dialog).getByText('reason')).toBeInTheDocument();
    expect(within(dialog).getByText('withheld')).toBeInTheDocument();
    expect(within(dialog).queryByText('[withheld]')).not.toBeInTheDocument();
    expect(within(dialog).getByText('schedule')).toBeInTheDocument();
    expect(within(dialog).getByText('k1-0123456789abcdef')).toBeInTheDocument();
    expect(within(dialog).getByText('4bf92f3577b34da6a3ce929d0e0e4736')).toBeInTheDocument();
  });

  it('verifies the shown events against their signatures and marks each one with its verdict', async () => {
    serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);
    const bodies = answerVerification(verification({
      checkedCount: 2,
      intactCount: 1,
      modifiedCount: 1,
      outcome: 'failure',
      failedEventIds: [11],
      verdicts: [{ eventId: 12, verdict: 'intact' }, { eventId: 11, verdict: 'modified' }],
    }));
    renderAt();
    await screen.findByText('subscription updated');

    fireEvent.click(screen.getByRole('button', { name: /Verify shown events/ }));

    expect(await screen.findByText('1 of 2 events failed verification')).toBeInTheDocument();
    expect(bodies[0]).toEqual({ from: '2026-10-01T00:00:00.000Z', to: '2026-10-14T23:59:59.999Z', eventIds: [12, 11] });
    expect(screen.getByText(/1 changed or forged; 0 signed with an integrity key this deployment does not hold/)).toHaveTextContent('#11');
    const rows = within(screen.getByRole('table', { name: 'Security audit events' })).getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('Intact')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Modified')).toBeInTheDocument();
  });

  it('verifies one event from its row', async () => {
    serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);
    const bodies = answerVerification(verification({
      checkedCount: 1,
      intactCount: 1,
      verdicts: [{ eventId: 12, verdict: 'intact' }],
    }));
    renderAt();

    fireEvent.click(await screen.findByRole('button', { name: 'Verify event 12' }));

    expect(await screen.findByText('1 of 1 event verified intact')).toBeInTheDocument();
    expect(bodies[0]).toMatchObject({ eventIds: [12] });
    expect(screen.getByText('Intact')).toBeInTheDocument();
    expect(screen.getAllByText('Not verified')).toHaveLength(1);
  });

  it('shows an event under an unknown key, and one not found, apart from a modified one', async () => {
    serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);
    answerVerification(verification({
      checkedCount: 1,
      unknownKeyCount: 1,
      outcome: 'failure',
      failedEventIds: [12],
      verdicts: [{ eventId: 12, verdict: 'unknown_key' }, { eventId: 11, verdict: 'not_found' }],
    }));
    renderAt();
    await screen.findByText('subscription updated');

    fireEvent.click(screen.getByRole('button', { name: /Verify shown events/ }));

    expect(await screen.findByText('1 of 1 event failed verification')).toBeInTheDocument();
    expect(screen.getByText(/0 changed or forged; 1 signed with an integrity key this deployment does not hold/))
      .toHaveTextContent('1 not found in the window');
    const rows = within(screen.getByRole('table', { name: 'Security audit events' })).getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('Unknown key')).toBeInTheDocument();
    expect(within(rows[1]).getByText('Not found')).toBeInTheDocument();
    expect(screen.queryByText('Modified')).not.toBeInTheDocument();
    expect(screen.queryByText('Intact')).not.toBeInTheDocument();
  });

  it('marks no event intact when the answer gives it no verdict, whatever the counts say', async () => {
    serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);
    answerVerification(verification({ checkedCount: 2, intactCount: 2, verdicts: null }));
    renderAt();
    await screen.findByText('subscription updated');

    fireEvent.click(screen.getByRole('button', { name: /Verify shown events/ }));

    expect(await screen.findAllByText('Unverified')).toHaveLength(2);
    expect(screen.queryByText('Intact')).not.toBeInTheDocument();
  });

  it('says a verification that checked nothing is no success', async () => {
    serveEvents([SUBSCRIPTION_UPDATED]);
    answerVerification(verification({ outcome: 'empty', verdicts: [{ eventId: 12, verdict: 'not_found' }] }));
    renderAt();

    fireEvent.click(await screen.findByRole('button', { name: 'Verify event 12' }));

    expect(await screen.findByText('Nothing was verified')).toBeInTheDocument();
    expect(screen.getByText(/this is not a success/)).toBeInTheDocument();
    expect(screen.queryByText(/verified intact/)).not.toBeInTheDocument();
    expect(screen.getByText('Not found')).toBeInTheDocument();
  });

  it('counts an event dated out of range as failing, and says when only the first failing ids are listed', async () => {
    serveEvents([SUBSCRIPTION_UPDATED, SIGN_IN_FAILED]);
    answerVerification(verification({
      checkedCount: 149,
      modifiedCount: 149,
      outOfRangeCount: 1,
      outcome: 'failure',
      failedEventIds: Array.from({ length: 100 }, (_, i) => i + 1),
    }));
    renderAt();
    await screen.findByText('subscription updated');

    fireEvent.click(screen.getByRole('button', { name: /Verify shown events/ }));

    expect(await screen.findByText('150 of 150 events failed verification')).toBeInTheDocument();
    expect(screen.getByText(/1 dated outside the range a genuine event can have/)).toHaveTextContent('The first 100 failing: #1, #2');
  });

  it('says the verification is busy when another one is running, without a failure toast', async () => {
    serveEvents([SUBSCRIPTION_UPDATED]);
    const busy = 'A verification of the security audit trail is already running; try again when it has finished.';
    answerVerification(() => HttpResponse.json({ title: 'Too Many Requests', detail: busy, status: 429 }, { status: 429 }));
    renderAt();

    fireEvent.click(await screen.findByRole('button', { name: 'Verify event 12' }));

    expect(await screen.findByText('Verification busy')).toBeInTheDocument();
    expect(screen.getByText(busy)).toBeInTheDocument();
    expect(screen.queryByText('The verification did not run')).not.toBeInTheDocument();
    expect(toast.error).not.toHaveBeenCalled();
    expect(screen.getByText('Not verified')).toBeInTheDocument();
  });

  it('says so on the page when the verification fails for another reason', async () => {
    serveEvents([SUBSCRIPTION_UPDATED]);
    answerVerification(() => HttpResponse.json({ title: 'Server error', detail: 'The verification could not be recorded.', status: 500 }, { status: 500 }));
    renderAt();

    fireEvent.click(await screen.findByRole('button', { name: 'Verify event 12' }));

    expect(await screen.findByText('The verification did not run')).toBeInTheDocument();
    expect(screen.getByText('The verification could not be recorded.')).toBeInTheDocument();
    expect(screen.queryByText('Verification busy')).not.toBeInTheDocument();
  });
});

describe('verdictsOf', () => {
  it('takes each requested event\'s verdict from the server, per id', () => {
    expect(verdictsOf([1, 2, 3, 4], verification({
      verdicts: [
        { eventId: 1, verdict: 'intact' },
        { eventId: 2, verdict: 'modified' },
        { eventId: 3, verdict: 'unknown_key' },
        { eventId: 4, verdict: 'not_found' },
      ],
    }))).toEqual({ 1: 'intact', 2: 'modified', 3: 'unknown_key', 4: 'not_found' });
  });

  it('claims nothing for an id without a verdict, or with one it does not know, even when every count is intact', () => {
    // More ids failed than the answer lists: an id past the listed ones is never taken for intact.
    expect(verdictsOf([1, 2, 3], verification({
      checkedCount: 3,
      intactCount: 3,
      verdicts: [{ eventId: 1, verdict: 'intact' }, { eventId: 2, verdict: 'tampered' }],
    }))).toEqual({ 1: 'intact', 2: 'unverified', 3: 'unverified' });
    expect(verdictsOf([7], verification({ checkedCount: 1, intactCount: 1, verdicts: null }))).toEqual({ 7: 'unverified' });
  });
});

describe('actorOf', () => {
  it('names a user and a key by id, and a userless caller by the start of its keyed hash', () => {
    expect(actorOf({ ...SUBSCRIPTION_UPDATED, actorKind: 'api_key', apiKeyId: 5 })).toBe('API key · user #3 · key #5');
    expect(actorOf({ ...SIGN_IN_FAILED, actorKind: 'mcp_caller', callerHash: 'abcdef0123456789' })).toBe('MCP caller · abcdef01…');
    expect(actorOf({ ...SIGN_IN_FAILED, actorKind: 'system' })).toBe('System');
  });
});

describe('detailsOf', () => {
  it('reads an object, says null for none and undefined for what cannot be read', () => {
    expect(detailsOf('{"a":1,"b":"[withheld]"}')).toEqual([['a', 1], ['b', '[withheld]']]);
    expect(detailsOf(null)).toBeNull();
    expect(detailsOf('[1,2]')).toBeUndefined();
    expect(detailsOf('not json')).toBeUndefined();
  });
});
