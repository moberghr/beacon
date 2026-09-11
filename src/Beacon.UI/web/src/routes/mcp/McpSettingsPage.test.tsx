import { fireEvent, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import McpSettingsPage, { FIELD_TAB, OVERRIDE_SECTIONS } from './McpSettingsPage';
import { renderWithProviders } from '@/test/render';
import { mswServer } from '../../../vitest.setup';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

/**
 * Spec mcp-project-settings, UI half: the settings page gains a scope selector. Global
 * scope shows the six new execution fields; a project scope shows per-field override
 * toggles seeded from the effective value, hides deployment-locked fields and names them
 * in a banner, PUTs only the override row, and surfaces the server's 409 setting-locked
 * reason (RFC 7807 title) instead of the generated client's fixed error message.
 */

const GLOBAL = {
  askSystemPrompt: null,
  globalInstruction: null,
  getContextDescription: null,
  queryDescription: null,
  getDocumentationDescription: null,
  askDescription: null,
  searchDescription: null,
  maxRowLimit: 1000,
  enforceReadOnly: true,
  enablePiiDetection: true,
  customPiiPatterns: [],
  enableSampleValueCollection: true,
  enableLearning: true,
  learningAutoApproveThreshold: 0.85,
  learningInjectionBudgetChars: 4000,
  learningSignalRetentionDays: 90,
  retainQueryContent: true,
  statementTimeoutSeconds: 30,
  maxResultBytes: 262144,
  maxExplainCost: null,
  maxConcurrentQueriesPerKey: 4,
  allowExplicitFeedbackContent: true,
};

const adminMe = http.get('*/beacon/api/auth/me', () =>
  HttpResponse.json({
    userId: 'mock-admin',
    displayName: 'Mock Admin',
    email: 'mock.admin@example.test',
    isAuthenticated: true,
    roles: ['Admin'],
  }),
);

const globalSettings = http.get('*/beacon/api/mcp/settings', () => HttpResponse.json(GLOBAL));

// Mutations prime the antiforgery cookie first; the global MSW setup fails on any
// unhandled request, so the prime endpoint needs a handler too.
const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ ok: true }));

const projectOneSettings = http.get('*/beacon/api/mcp/projects/1/settings', () =>
  HttpResponse.json({
    projectId: 1,
    overrides: { maxRowLimit: 50 },
    effective: { ...GLOBAL, maxRowLimit: 50, statementTimeoutSeconds: 20 },
    lockedFields: ['EnforceReadOnly'],
    clampedFields: ['StatementTimeoutSeconds'],
  }),
);

async function renderPage() {
  renderWithProviders(<McpSettingsPage />);
  await screen.findByText('Save settings');
}

async function switchToProjectOne() {
  // The scope selector lists projects from /beacon/api/projects (default handler: Acme = 1).
  await screen.findByRole('option', { name: 'Project: Acme Analytics' });
  fireEvent.change(screen.getByLabelText('Settings scope'), {
    target: { value: '1' },
  });
  await screen.findByText('Save overrides');
}

describe('McpSettingsPage', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    mswServer.use(adminMe, globalSettings, csrf, projectOneSettings);
  });

  it('global scope shows the six execution fields on the Guardrails tab', async () => {
    await renderPage();
    fireEvent.click(screen.getByText('Guardrails'));

    expect(screen.getByLabelText('Statement timeout (s)')).toHaveValue(30);
    expect(screen.getByLabelText('Max result size (bytes)')).toHaveValue(262144);
    expect(screen.getByLabelText('Max concurrent queries per API key')).toHaveValue(4);
    expect(screen.getByLabelText('Max EXPLAIN cost (blank = no limit)')).toHaveValue(null);
    expect(screen.getByLabelText(/Retain question and SQL text/)).toBeChecked();
    expect(screen.getByLabelText(/Allow the feedback tool/)).toBeChecked();
  });

  it('project scope shows override state, hides locked fields and names them in a banner', async () => {
    await renderPage();
    await switchToProjectOne();

    // Overridden field: toggle on, value from the override row.
    expect(screen.getByLabelText('Override Max row limit')).toBeChecked();
    expect(screen.getByLabelText('Max row limit')).toHaveValue(50);
    expect(screen.getByLabelText('Max row limit')).toBeEnabled();

    // Inherited field: toggle off, control disabled, shows the effective value.
    expect(screen.getByLabelText('Override Statement timeout (s)')).not.toBeChecked();
    expect(screen.getByLabelText('Statement timeout (s)')).toBeDisabled();
    expect(screen.getByLabelText('Statement timeout (s)')).toHaveValue(20);

    // Locked field is not rendered at all, and the banner names it.
    expect(screen.queryByLabelText('Override Enforce read-only queries')).toBeNull();
    expect(screen.queryByText('Enforce read-only queries')).toBeNull();
    expect(screen.getByText('Locked by deployment configuration')).toBeInTheDocument();
    expect(screen.getByText(/EnforceReadOnly is pinned by Beacon:Mcp/)).toBeInTheDocument();

    // Clamped field is flagged.
    expect(screen.getByText('Clamped to deployment ceiling')).toBeInTheDocument();
  });

  it('an override whose effective value is null (no EXPLAIN cost limit) can still be turned on', async () => {
    await renderPage();
    await switchToProjectOne();

    const toggle = screen.getByLabelText('Override Max EXPLAIN cost');
    expect(toggle).not.toBeChecked();
    expect(screen.getByLabelText('Max EXPLAIN cost')).toBeDisabled();

    fireEvent.click(toggle);

    // Seeded blank (NaN) instead of null, so the row does not snap back to "inherit" and the admin
    // must type a value — Save refuses a blank override.
    expect(screen.getByLabelText('Override Max EXPLAIN cost')).toBeChecked();
    expect(screen.getByLabelText('Max EXPLAIN cost')).toBeEnabled();
    expect(screen.getByLabelText('Max EXPLAIN cost')).toHaveValue(null);

    fireEvent.click(screen.getByText('Save overrides'));
    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringContaining('Max EXPLAIN cost')));
  });

  it('global Save PUTs every field including the six execution fields, blank EXPLAIN cost as null', async () => {
    let body: { data: Record<string, unknown> } | undefined;
    mswServer.use(
      http.put('*/beacon/api/mcp/settings', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await renderPage();
    fireEvent.click(screen.getByText('Guardrails'));
    fireEvent.change(screen.getByLabelText('Statement timeout (s)'), { target: { value: '45' } });
    fireEvent.click(screen.getByText('Save settings'));

    await waitFor(() => expect(body).toBeDefined());
    expect(body!.data).toMatchObject({
      maxRowLimit: 1000,
      enforceReadOnly: true,
      retainQueryContent: true,
      statementTimeoutSeconds: 45,
      maxResultBytes: 262144,
      maxExplainCost: null,
      maxConcurrentQueriesPerKey: 4,
      allowExplicitFeedbackContent: true,
    });
    await waitFor(() => expect(toast.success).toHaveBeenCalledWith('MCP settings saved.'));
  });

  it('the per-project override table mirrors the global Guardrails tab field for field', () => {
    // Form keys that are text encodings of a data field.
    const formKeyToDataKey: Record<string, string> = {
      customPiiPatternsText: 'customPiiPatterns',
      maxExplainCostText: 'maxExplainCost',
    };
    // Overridable per project but with no toggle on the global form yet (see onSubmit's
    // enableSampleValueCollection comment) — the one intentional asymmetry.
    const overrideOnly = ['enableSampleValueCollection'];

    const guardrailKeys = Object.entries(FIELD_TAB)
      .filter(([, tab]) => tab === 'guardrails')
      .map(([key]) => formKeyToDataKey[key] ?? key);
    const overrideKeys = OVERRIDE_SECTIONS.flatMap(s => s.fields.map(f => f.key as string));

    expect(new Set(overrideKeys)).toEqual(new Set([...guardrailKeys, ...overrideOnly]));
    expect(overrideKeys.length).toBe(new Set(overrideKeys).size);
  });

  it('saving project overrides PUTs the override row, seeded from the effective value', async () => {
    let body: { data: Record<string, unknown> } | undefined;
    mswServer.use(
      http.put('*/beacon/api/mcp/projects/1/settings', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await renderPage();
    await switchToProjectOne();

    // Turning an override on seeds it with the effective value (20), then the admin edits it.
    fireEvent.click(screen.getByLabelText('Override Statement timeout (s)'));
    const timeout = screen.getByLabelText('Statement timeout (s)');
    expect(timeout).toBeEnabled();
    expect(timeout).toHaveValue(20);
    fireEvent.change(timeout, { target: { value: '45' } });

    fireEvent.click(screen.getByText('Save overrides'));

    await waitFor(() => expect(body).toBeDefined());
    expect(body!.data.maxRowLimit).toBe(50);
    expect(body!.data.statementTimeoutSeconds).toBe(45);
    // Inherited and locked fields carry no override.
    expect(body!.data.maxResultBytes ?? null).toBeNull();
    expect(body!.data.enforceReadOnly ?? null).toBeNull();
    await waitFor(() =>
      expect(toast.success).toHaveBeenCalledWith('Project overrides saved for Acme Analytics.'),
    );
  });

  it('a 409 setting-locked response surfaces the server title in the error toast', async () => {
    mswServer.use(
      http.put('*/beacon/api/mcp/projects/1/settings', () =>
        HttpResponse.json(
          {
            type: '/errors/setting-locked',
            title:
              "Setting 'EnforceReadOnly' is locked by deployment configuration 'ForceReadOnly'.",
            status: 409,
          },
          {
            status: 409,
            headers: { 'Content-Type': 'application/problem+json' },
          },
        ),
      ),
    );

    await renderPage();
    await switchToProjectOne();
    fireEvent.click(screen.getByText('Save overrides'));

    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith(
        "Setting 'EnforceReadOnly' is locked by deployment configuration 'ForceReadOnly'.",
      ),
    );
    expect(toast.success).not.toHaveBeenCalled();
  });
});
