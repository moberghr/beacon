import { fireEvent, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'sonner';
import McpSettingsPage, { FIELD_TAB, OVERRIDE_SECTIONS } from './McpSettingsPage';
import { renderWithProviders } from '@/test/render';
import { functionalOnly, permissionsForRoles, permissionsHandler } from '@/test/permissions';
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
  enableValueGrounding: true,
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
  // The server's row has more than the page shows; a save must post these back as read.
  enableSelfConsistency: true,
  selfConsistencyCandidateCount: 7,
  enableEvalJudge: true,
  enableSemanticRetrieval: false,
  exemplarTopK: 9,
  enableReplayVerification: false,
  learningReplayMinFlips: 3,
  enableContextualRetrieval: true,
  docChunkWindowSentences: 8,
  docChunkOverlapSentences: 2,
  glossaryTopK: 11,
  docChunkTopK: 13,
  enableGoldenExemplars: false,
  goldenExemplarTopK: 6,
  goldenExemplarBudgetChars: 3333,
  valueGroundingMaxProbes: 5,
  enableSemanticLint: false,
  selfConsistencyMinTables: 4,
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
  // The scope picker searches /beacon/api/projects (default handler: Acme = 1).
  fireEvent.click(screen.getByRole('button', { name: 'Settings scope' }));
  fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
  await screen.findByText('Save overrides');
}

describe('McpSettingsPage', () => {
  beforeEach(() => {
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.error).mockClear();
    mswServer.use(adminMe, permissionsForRoles(['Admin']), globalSettings, csrf, projectOneSettings);
  });

  it('is read-only for an Auditor: no save, disabled fields, a project scope still readable', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Auditor'] }));
    renderWithProviders(<McpSettingsPage />);

    expect(await screen.findByText('Read only')).toBeInTheDocument();
    expect(screen.queryByText('Save settings')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Guardrails'));
    expect(screen.getByLabelText('Statement timeout (s)')).toBeDisabled();
    expect(screen.getByLabelText(/Retain question and SQL text/)).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Settings scope' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    expect(await screen.findByLabelText('Override Max row limit')).toBeDisabled();
    expect(screen.getByLabelText('Max row limit')).toHaveValue(50);
    expect(screen.queryByText('Save overrides')).not.toBeInTheDocument();
  });

  it('lets a SecurityAdmin who is not an Admin change the settings', async () => {
    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    await renderPage();

    expect(screen.queryByText('Read only')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Guardrails'));
    expect(screen.getByLabelText('Statement timeout (s)')).toBeEnabled();
  });

  it('sends a user holding neither permission home', async () => {
    mswServer.use(permissionsHandler({ permissions: ['UserAdmin'] }));
    renderWithProviders(<McpSettingsPage />);

    await waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith('The Auditor or SecurityAdmin permission is required for this page.'),
    );
    expect(screen.queryByText('Save settings')).not.toBeInTheDocument();
  });

  it('global scope shows the six execution fields on the Guardrails tab', async () => {
    await renderPage();
    fireEvent.click(screen.getByText('Guardrails'));

    expect(screen.getByLabelText('Statement timeout (s)')).toHaveValue(30);
    expect(screen.getByLabelText('Max result size (bytes)')).toHaveValue(262144);
    expect(screen.getByLabelText('Max concurrent queries per API key')).toHaveValue(4);
    expect(screen.getByLabelText(/^Max EXPLAIN cost \(blank = no limit\)/)).toHaveValue(null);
    expect(screen.getByText(/Applies to PostgreSQL data sources/)).toBeInTheDocument();
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
      enableSampleValueCollection: true,
      enableValueGrounding: true,
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

    const guardrailKeys = Object.entries(FIELD_TAB)
      .filter(([, tab]) => tab === 'guardrails')
      .map(([key]) => formKeyToDataKey[key] ?? key);
    const overrideKeys = OVERRIDE_SECTIONS.flatMap(s => s.fields.map(f => f.key as string));

    expect(new Set(overrideKeys)).toEqual(new Set(guardrailKeys));
    expect(overrideKeys.length).toBe(new Set(overrideKeys).size);
  });

  it('global Save posts back every key of the loaded settings, those the page does not show as read', async () => {
    let body: { data: Record<string, unknown> } | undefined;
    mswServer.use(
      http.put('*/beacon/api/mcp/settings', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await renderPage();
    fireEvent.click(screen.getByText('Save settings'));

    await waitFor(() => expect(body).toBeDefined());
    expect(Object.keys(body!.data).sort()).toEqual(Object.keys(GLOBAL).sort());
    // Nothing edited: every value goes back exactly as it was read.
    expect(body!.data).toEqual(GLOBAL);
  });

  it('global Save sends the stored values back, a locked field as the lock pins it, and names a ceiling that applies', async () => {
    let body: { data: Record<string, unknown> } | undefined;
    mswServer.use(
      http.get('*/beacon/api/mcp/settings', () =>
        HttpResponse.json({
          ...GLOBAL,
          maxRowLimit: 2000,
          enforceReadOnly: true,
          stored: { ...GLOBAL, maxRowLimit: 9000, enforceReadOnly: false },
          lockedFields: ['EnforceReadOnly'],
          clampedFields: ['MaxRowLimit'],
        }),
      ),
      http.put('*/beacon/api/mcp/settings', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await renderPage();
    fireEvent.click(screen.getByText('Guardrails'));
    expect(screen.getByText(/Saved above a deployment ceiling/).textContent).toContain('maxRowLimit (2000)');
    fireEvent.click(screen.getByText('Save settings'));

    await waitFor(() => expect(body).toBeDefined());
    expect(body!.data).toEqual({ ...GLOBAL, maxRowLimit: 9000, enforceReadOnly: true });
  });

  describe('row data sent to the LLM provider', () => {
    it('shows sample-value collection and value grounding on the Guardrails tab, and saves a change', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        http.get('*/beacon/api/mcp/settings', () =>
          HttpResponse.json({ ...GLOBAL, enableSampleValueCollection: false, enableValueGrounding: true })),
        http.put('*/beacon/api/mcp/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      const sampling = screen.getByLabelText('Collect sample values');
      const grounding = screen.getByLabelText('Ground questions in live values (value grounding)');
      expect(sampling).not.toBeChecked();
      expect(sampling).toBeEnabled();
      expect(grounding).toBeChecked();
      expect(grounding).toBeEnabled();
      expect(screen.queryByText(/ForbidRowDataToLlm/)).not.toBeInTheDocument();

      fireEvent.click(sampling);
      fireEvent.click(grounding);
      fireEvent.click(screen.getByText('Save settings'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data).toMatchObject({ enableSampleValueCollection: true, enableValueGrounding: false });
    });

    it('stays off and disabled while the deployment forbids row data, saying where it is unlocked', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        // The GET resolves the lock: both read as off.
        http.get('*/beacon/api/mcp/settings', () =>
          HttpResponse.json({ ...GLOBAL, enableSampleValueCollection: false, enableValueGrounding: false, enableEvalJudge: false })),
        http.get('*/beacon/api/mcp/settings/locks', () =>
          HttpResponse.json({ lockedFields: ['EnableEvalJudge', 'EnableSampleValueCollection', 'EnableValueGrounding'] })),
        http.put('*/beacon/api/mcp/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      await waitFor(() => expect(screen.getByLabelText('Collect sample values')).toBeDisabled());
      expect(screen.getByLabelText('Collect sample values')).not.toBeChecked();
      expect(screen.getByLabelText('Ground questions in live values (value grounding)')).toBeDisabled();
      expect(screen.getByText(/Off by the deployment/)).toHaveTextContent(/Beacon:Mcp:ForbidRowDataToLlm/);
      expect(screen.getByLabelText('Statement timeout (s)')).toBeEnabled();
      fireEvent.click(screen.getByText('Save settings'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data).toMatchObject({ enableSampleValueCollection: false, enableValueGrounding: false });
    });

    it('a project overrides value grounding like sample-value collection', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        http.put('*/beacon/api/mcp/projects/1/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      await switchToProjectOne();

      expect(screen.getByLabelText('Override Collect sample values')).not.toBeChecked();
      const toggle = screen.getByLabelText('Override Ground questions in live values (value grounding)');
      expect(toggle).not.toBeChecked();
      fireEvent.click(toggle);
      // Seeded with the effective value (on), then turned off for this project.
      const grounding = screen.getByLabelText('Ground questions in live values (value grounding)');
      expect(grounding).toBeChecked();
      fireEvent.click(grounding);
      fireEvent.click(screen.getByText('Save overrides'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data.enableValueGrounding).toBe(false);
      expect(body!.data.enableSampleValueCollection ?? null).toBeNull();
    });

    it('a project under the lock shows neither switch, and the banner says why', async () => {
      mswServer.use(
        http.get('*/beacon/api/mcp/projects/1/settings', () =>
          HttpResponse.json({
            projectId: 1,
            overrides: {},
            effective: { ...GLOBAL, enableSampleValueCollection: false, enableValueGrounding: false, enableEvalJudge: false },
            lockedFields: ['EnableEvalJudge', 'EnableSampleValueCollection', 'EnableValueGrounding'],
            clampedFields: [],
          }),
        ),
      );
      await renderPage();
      await switchToProjectOne();

      expect(screen.queryByLabelText('Override Collect sample values')).toBeNull();
      expect(screen.queryByLabelText('Override Ground questions in live values (value grounding)')).toBeNull();
      expect(screen.getByText(/pinned by Beacon:Mcp/)).toHaveTextContent(
        /Beacon:Mcp:ForbidRowDataToLlm keeps every value read from the data away from the LLM provider/,
      );
    });
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

  describe('query content retention', () => {
    const requireContent = http.get('*/beacon/api/mcp/settings/locks', () =>
      HttpResponse.json({
        lockedFields: ['RetainQueryContent'],
        locks: [{ fieldName: 'RetainQueryContent', lockName: 'Audit:RequireContent' }],
      }));

    it('stays on and disabled while the deployment requires audit content, saying which lock and where it is unlocked', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        requireContent,
        http.put('*/beacon/api/mcp/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      await waitFor(() => expect(screen.getByLabelText(/Retain question and SQL text/)).toBeDisabled());
      expect(screen.getByLabelText(/Retain question and SQL text/)).toBeChecked();
      expect(screen.getByText(/Kept on by the deployment/)).toHaveTextContent(/Beacon:Mcp:Audit:RequireContent/);
      expect(screen.getByText(/Kept on by the deployment/)).toHaveTextContent(/only the host's configuration can/);
      expect(screen.getByLabelText('Statement timeout (s)')).toBeEnabled();
      fireEvent.click(screen.getByText('Save settings'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data).toMatchObject({ retainQueryContent: true });
    });

    it('says the deployment keeps it off while content retention is forbidden', async () => {
      mswServer.use(
        http.get('*/beacon/api/mcp/settings', () => HttpResponse.json({ ...GLOBAL, retainQueryContent: false })),
        http.get('*/beacon/api/mcp/settings/locks', () =>
          HttpResponse.json({
            lockedFields: ['RetainQueryContent'],
            locks: [{ fieldName: 'RetainQueryContent', lockName: 'ForceNoContentRetention' }],
          })),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      await waitFor(() => expect(screen.getByLabelText(/Retain question and SQL text/)).toBeDisabled());
      expect(screen.getByLabelText(/Retain question and SQL text/)).not.toBeChecked();
      expect(screen.getByText(/Off by the deployment/)).toHaveTextContent(/Beacon:Mcp:ForceNoContentRetention/);
      expect(screen.queryByText(/Kept on by the deployment/)).not.toBeInTheDocument();
    });

    it('shows no lock note while nothing pins it', async () => {
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      expect(screen.getByLabelText(/Retain question and SQL text/)).toBeEnabled();
      expect(screen.queryByText(/Kept on by the deployment|Off by the deployment \(/)).not.toBeInTheDocument();
    });

    it('a project under the audit content lock shows no switch, and the banner names the lock', async () => {
      mswServer.use(
        requireContent,
        http.get('*/beacon/api/mcp/projects/1/settings', () =>
          HttpResponse.json({
            projectId: 1,
            overrides: {},
            effective: GLOBAL,
            lockedFields: ['RetainQueryContent'],
            clampedFields: [],
          }),
        ),
      );
      await renderPage();
      await switchToProjectOne();

      expect(screen.queryByLabelText(/Override Retain question and SQL text/)).toBeNull();
      expect(await screen.findByText(/pinned by Beacon:Mcp/)).toHaveTextContent(
        /Beacon:Mcp:Audit:RequireContent keeps the question and SQL text of every call in the audit/,
      );
    });
  });

  describe('read-only enforcement and PII detection', () => {
    const securityAdmin = permissionsHandler({ permissions: ['SecurityAdmin'] });

    function serveProjectTwo(overrides: Record<string, unknown>, global: Record<string, unknown>) {
      mswServer.use(
        http.get('*/beacon/api/mcp/settings', () => HttpResponse.json({ ...GLOBAL, ...global })),
        http.get('*/beacon/api/mcp/projects/2/settings', () =>
          HttpResponse.json({
            projectId: 2,
            overrides,
            effective: { ...GLOBAL, ...global, ...overrides },
            lockedFields: [],
            clampedFields: [],
          }),
        ),
      );
    }

    async function switchToProjectTwo() {
      fireEvent.click(screen.getByRole('button', { name: 'Settings scope' }));
      fireEvent.click(await screen.findByRole('option', { name: 'Beta Pipeline' }));
      await screen.findByText('Save overrides');
    }

    it('cannot be turned off globally by a SecurityAdmin who is not an Admin; the save keeps them on', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        securityAdmin,
        http.put('*/beacon/api/mcp/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      expect(screen.getByLabelText('Enforce read-only queries')).toBeDisabled();
      expect(screen.getByLabelText('Enable PII detection')).toBeDisabled();
      expect(screen.getByText('Only an Admin can turn read-only enforcement or PII detection off.')).toBeInTheDocument();
      fireEvent.click(screen.getByText('Save settings'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data).toMatchObject({ enforceReadOnly: true, enablePiiDetection: true });
    });

    it('can be turned on globally by a SecurityAdmin who is not an Admin', async () => {
      mswServer.use(
        securityAdmin,
        http.get('*/beacon/api/mcp/settings', () => HttpResponse.json({ ...GLOBAL, enforceReadOnly: false })),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));
      expect(screen.getByLabelText('Enforce read-only queries')).toBeEnabled();
      expect(screen.getByLabelText('Enable PII detection')).toBeDisabled();
    });

    it('lets an Admin turn them off globally when the deployment does not lock them', async () => {
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      expect(screen.getByLabelText('Enforce read-only queries')).toBeEnabled();
      expect(screen.getByLabelText('Enable PII detection')).toBeEnabled();
      expect(screen.queryByText(/Locked on by the deployment/)).not.toBeInTheDocument();
    });

    it('stay on for an Admin too while the deployment locks them, with a note saying where they are unlocked', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      mswServer.use(
        http.get('*/beacon/api/mcp/settings/locks', () =>
          HttpResponse.json({ lockedFields: ['EnablePiiDetection', 'EnforceReadOnly'] })),
        http.put('*/beacon/api/mcp/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      fireEvent.click(screen.getByText('Guardrails'));

      await waitFor(() => expect(screen.getByLabelText('Enforce read-only queries')).toBeDisabled());
      expect(screen.getByLabelText('Enable PII detection')).toBeDisabled();
      expect(screen.getByLabelText(/Retain question and SQL text/)).toBeEnabled();
      expect(screen.getByText(/Locked on by the deployment/)).toHaveTextContent(/only the host's configuration can/);
      expect(screen.queryByText('Only an Admin can turn read-only enforcement or PII detection off.')).not.toBeInTheDocument();
      fireEvent.click(screen.getByText('Save settings'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data).toMatchObject({ enforceReadOnly: true, enablePiiDetection: true });
    });

    it('keeps a SecurityAdmin from turning off or clearing a project override that would loosen them', async () => {
      // Read-only is pinned on while the global value is off; PII detection is pinned off.
      serveProjectTwo({ enforceReadOnly: true, enablePiiDetection: false }, { enforceReadOnly: false });
      mswServer.use(securityAdmin);
      await renderPage();
      await switchToProjectTwo();

      expect(screen.getByLabelText('Override Enforce read-only queries')).toBeDisabled();
      expect(screen.getByLabelText('Enforce read-only queries')).toBeDisabled();
      expect(screen.getByText(/only an admin can turn read-only enforcement or pii detection off for a project/i))
        .toBeInTheDocument();
      // Already pinned off: turning it on, back off, or inheriting the global value loosens nothing.
      expect(screen.getByLabelText('Override Enable PII detection')).toBeEnabled();
      fireEvent.click(screen.getByLabelText('Enable PII detection'));
      expect(screen.getByLabelText('Enable PII detection')).toBeChecked();
      expect(screen.getByLabelText('Enable PII detection')).toBeEnabled();
    });

    it('lets a SecurityAdmin clear an override pinning read-only on while the global value is on', async () => {
      serveProjectTwo({ enforceReadOnly: true }, {});
      mswServer.use(securityAdmin);
      await renderPage();
      await switchToProjectTwo();

      expect(screen.getByLabelText('Override Enforce read-only queries')).toBeEnabled();
      expect(screen.getByLabelText('Enforce read-only queries')).toBeDisabled();
    });

    it('pins an inherited switch on when a SecurityAdmin overrides it, even while the global value is off', async () => {
      let body: { data: Record<string, unknown> } | undefined;
      serveProjectTwo({}, { enforceReadOnly: false });
      mswServer.use(
        securityAdmin,
        http.put('*/beacon/api/mcp/projects/2/settings', async ({ request }) => {
          body = (await request.json()) as typeof body;
          return new HttpResponse(null, { status: 204 });
        }),
      );
      await renderPage();
      await switchToProjectTwo();

      fireEvent.click(screen.getByLabelText('Override Enforce read-only queries'));
      expect(screen.getByLabelText('Enforce read-only queries')).toBeChecked();
      expect(screen.getByLabelText('Enforce read-only queries')).toBeDisabled();
      fireEvent.click(screen.getByText('Save overrides'));

      await waitFor(() => expect(body).toBeDefined());
      expect(body!.data.enforceReadOnly).toBe(true);
    });

    it('lets an Admin turn off or clear any project override', async () => {
      serveProjectTwo({ enforceReadOnly: true, enablePiiDetection: true }, { enforceReadOnly: false });
      await renderPage();
      await switchToProjectTwo();

      expect(screen.getByLabelText('Override Enforce read-only queries')).toBeEnabled();
      expect(screen.getByLabelText('Enforce read-only queries')).toBeEnabled();
      expect(screen.getByLabelText('Enable PII detection')).toBeEnabled();
      expect(screen.queryByText(/for a project/)).not.toBeInTheDocument();
    });
  });

  it('offers a user with no Viewer or Editor role only the global scope, since picking a project lists them', async () => {
    mswServer.use(functionalOnly('SecurityAdmin'));
    await renderPage();

    expect(screen.queryByRole('button', { name: 'Settings scope' })).not.toBeInTheDocument();
  });

  it('does not offer saving the global defaults in gateway mode, but keeps the project overrides editable', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true, gatewayMode: true }));
    renderWithProviders(<McpSettingsPage />);

    expect(await screen.findByText('Read only')).toBeInTheDocument();
    expect(screen.queryByText('Save settings')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Guardrails'));
    expect(screen.getByLabelText('Statement timeout (s)')).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Settings scope' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Acme Analytics' }));
    expect(await screen.findByText('Save overrides')).toBeInTheDocument();
    expect(screen.getByLabelText('Override Max row limit')).toBeEnabled();
  });
  it('describes only the tools the server serves: the MCP gateway has no SQL tools', async () => {
    mswServer.use(
      permissionsHandler({ isAdmin: true, gatewayMode: true }),
      http.get('*/beacon/api/mcp/tools', () => HttpResponse.json({ toolNames: ['get_context', 'get_documentation', 'search'] })),
    );
    renderWithProviders(<McpSettingsPage />);

    expect(await screen.findByText('Read only')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByLabelText('Ask tool system prompt')).not.toBeInTheDocument());
    expect(screen.getByLabelText('Global instruction')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Tool descriptions'));
    for (const tool of ['get_context', 'get_documentation', 'search']) {
      expect(screen.getByLabelText(tool)).toBeInTheDocument();
    }
    for (const tool of ['ask', 'query']) {
      expect(screen.queryByLabelText(tool)).not.toBeInTheDocument();
    }
  });

  it('describes every tool a full Beacon serves', async () => {
    mswServer.use(permissionsHandler({ isAdmin: true }));
    renderWithProviders(<McpSettingsPage />);

    expect(await screen.findByLabelText('Ask tool system prompt')).toBeInTheDocument();
    fireEvent.click(screen.getByText('Tool descriptions'));
    for (const tool of ['get_context', 'ask', 'query', 'get_documentation', 'search']) {
      expect(screen.getByLabelText(tool)).toBeInTheDocument();
    }
  });
});
