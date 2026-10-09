import { describe, it, expect, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { ParameterType } from '@/lib/enums';
import { AddSubscriptionDialog } from './AddSubscriptionDialog';

function stubQueryEndpoints(queryId: number, steps: unknown[] = []) {
  mswServer.use(
    http.get('*/beacon/api/queries', () =>
      HttpResponse.json({
        items: [
          {
            queryId,
            name: `Query ${queryId}`,
            description: null,
            subscriptionsCount: 0,
          },
        ],
        totalCount: 1,
        page: 1,
        pageSize: 20,
      }),
    ),
    http.get(`*/beacon/api/queries/${queryId}`, () =>
      HttpResponse.json({
        queryId,
        name: `Query ${queryId}`,
        description: null,
        steps,
      }),
    ),
  );
}

function stubRecipients(items: unknown[]) {
  mswServer.use(
    http.get('*/beacon/api/recipients', () =>
      HttpResponse.json({ items, totalCount: items.length, pageCount: items.length > 0 ? 1 : 0 }),
    ),
  );
}

const OPS_RECIPIENT = {
  id: 7,
  name: 'Ops',
  description: null,
  destination: 'ops@example.com',
  notificationType: 2,
  headersJson: null,
  bodyTemplate: null,
  subscriptionCount: 0,
};

function captureCreate() {
  const captured: { body: unknown } = { body: null };
  mswServer.use(
    http.post('*/beacon/api/subscriptions', async ({ request }) => {
      captured.body = await request.json();
      return HttpResponse.json({ success: true, message: null });
    }),
  );
  return captured;
}

/** Next waits for the picked query's parameters, so let its detail load first. */
async function waitForQuery(queryId: number) {
  await screen.findByText(`Query ${queryId}`);
}

function next() {
  fireEvent.click(screen.getByTestId('stepper-next'));
}

async function submit() {
  fireEvent.click(await screen.findByRole('button', { name: /create subscription/i }));
}

describe('AddSubscriptionDialog (multi-step)', () => {
  it('walks through steps and POSTs the new subscription', async () => {
    stubQueryEndpoints(12);
    stubRecipients([OPS_RECIPIENT]);
    const captured = captureCreate();

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={12} />,
    );

    // Query — preselected via initialQueryId; advance.
    await waitForQuery(12);
    next();

    // Notify — pick Ops.
    await screen.findByText(/Ops/);
    fireEvent.click(screen.getByRole('checkbox', { name: /Ops/i }));
    next();

    // Results, then Anomaly — defaults.
    await screen.findByText(/Include results as attachment/i);
    next();
    await screen.findByText(/Enable anomaly detection/i);
    next();

    // Review — submit.
    await submit();

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured.body).toMatchObject({
      queryId: 12,
      cronExpression: '0 9 * * *',
      recipientIds: [7],
      notificationTrigger: 1,
      minimumRowCount: null,
      resultAttachmentType: null,
      parameters: [],
      anomalyConfig: null,
    });
  });

  it('sends the notification trigger, row threshold, attachment format and anomaly settings', async () => {
    stubQueryEndpoints(12);
    stubRecipients([OPS_RECIPIENT]);
    const captured = captureCreate();

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={12} />,
    );

    await waitForQuery(12);
    next();

    // Notify — always send, but only from 10 rows up.
    await screen.findByText(/Ops/);
    fireEvent.change(screen.getByLabelText(/send notification/i), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText(/minimum row count/i), { target: { value: '10' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /Ops/i }));
    next();

    // Results — XLSX attachment.
    fireEvent.click(await screen.findByRole('checkbox', { name: /include results as attachment/i }));
    fireEvent.change(await screen.findByLabelText(/attachment format/i), { target: { value: '2' } });
    next();

    // Anomaly — IQR over 60 days, increases only.
    fireEvent.click(await screen.findByRole('checkbox', { name: /enable anomaly detection/i }));
    fireEvent.change(await screen.findByLabelText(/detection method/i), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText(/lookback/i), { target: { value: '60' } });
    fireEvent.click(screen.getByRole('checkbox', { name: /unusual decrease/i }));
    next();

    await submit();

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured.body).toMatchObject({
      notificationTrigger: 2,
      minimumRowCount: 10,
      includeAttachment: true,
      resultAttachmentType: 2,
      anomalyConfig: {
        detectionMethod: 2,
        sensitivity: 2,
        lookbackDays: 60,
        minimumDataPoints: 7,
        alertOnIncrease: true,
        alertOnDecrease: false,
      },
    });
  });

  it('requires an anomaly alert direction when detection is on', async () => {
    stubQueryEndpoints(12);
    stubRecipients([OPS_RECIPIENT]);

    renderWithProviders(
      <AddSubscriptionDialog open onClose={vi.fn()} initialQueryId={12} />,
    );

    await waitForQuery(12);
    next();
    await screen.findByText(/Ops/);
    fireEvent.click(screen.getByRole('checkbox', { name: /Ops/i }));
    next();
    await screen.findByText(/Include results as attachment/i);
    next();

    fireEvent.click(await screen.findByRole('checkbox', { name: /enable anomaly detection/i }));
    fireEvent.click(await screen.findByRole('checkbox', { name: /unusual increase/i }));
    fireEvent.click(screen.getByRole('checkbox', { name: /unusual decrease/i }));
    next();

    expect(await screen.findByText(/Alert on an increase, a decrease, or both/i)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /create subscription/i })).toBeNull();
  });

  it('asks for each query parameter once and sends the values', async () => {
    stubQueryEndpoints(12, [
      {
        stepOrder: 1,
        parameters: [
          { name: 'From', type: ParameterType.Number, description: 'Lower bound', placeholder: '@from' },
          { name: 'Region', type: ParameterType.String, description: null, placeholder: '@region' },
        ],
      },
      {
        stepOrder: 2,
        parameters: [
          { name: 'Region', type: ParameterType.String, description: null, placeholder: '@region' },
        ],
      },
    ]);
    stubRecipients([]);
    const captured = captureCreate();

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={12} />,
    );

    await waitForQuery(12);
    await screen.findByText(/Query parameters/i);
    expect(screen.getAllByText('@region')).toHaveLength(1);

    // Blank values block the step.
    next();
    expect(await screen.findAllByText('Required')).toHaveLength(2);

    fireEvent.change(screen.getByLabelText(/^From/), { target: { value: '5' } });
    fireEvent.change(screen.getByLabelText(/^Region/), { target: { value: 'EU' } });
    next();

    // Notify — no recipients needed when a task is created instead.
    await screen.findByText(/No recipients yet/i);
    fireEvent.click(screen.getByRole('checkbox', { name: /create a task and keep it open/i }));
    next();
    await screen.findByText(/Include results as attachment/i);
    next();
    await screen.findByText(/Enable anomaly detection/i);
    next();

    await submit();

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured.body).toMatchObject({
      parameters: [
        { queryPlaceholder: '@from', value: '5' },
        { queryPlaceholder: '@region', value: 'EU' },
      ],
    });
  });

  it('blocks advancing past Notify step when no recipient is picked', async () => {
    stubQueryEndpoints(5);
    stubRecipients([]);

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={5} />,
    );

    await waitForQuery(5);
    next();

    // On Notify step now — try to advance without selecting any.
    await screen.findByText(/No recipients yet/i);
    next();

    await waitFor(() => {
      expect(screen.getByText(/Pick at least one recipient/i)).toBeInTheDocument();
    });

    expect(onClose).not.toHaveBeenCalled();
  });

  it('allows no recipients when a task is created instead', async () => {
    stubQueryEndpoints(5);
    stubRecipients([]);
    const captured = captureCreate();

    const onClose = vi.fn();
    renderWithProviders(
      <AddSubscriptionDialog open onClose={onClose} initialQueryId={5} />,
    );

    await waitForQuery(5);
    next();
    await screen.findByText(/No recipients yet/i);

    // The info tip spells out the task lifecycle behind the checkbox.
    expect(screen.getByRole('button', { name: /more info/i })).toHaveAccessibleDescription(
      /returns no rows resolves the task automatically/i,
    );

    // Blocked first, then ticking the task option lifts the requirement and clears the error.
    next();
    await screen.findByText(/Pick at least one recipient/i);
    fireEvent.click(screen.getByRole('checkbox', { name: /create a task and keep it open/i }));
    await waitFor(() => {
      expect(screen.queryByText(/Pick at least one recipient/i)).toBeNull();
    });

    next();
    await screen.findByText(/Include results as attachment/i);
    next();
    await screen.findByText(/Enable anomaly detection/i);
    next();
    await submit();

    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });

    expect(captured.body).toMatchObject({
      queryId: 5,
      recipientIds: [],
      createTasks: true,
    });
  });
});
