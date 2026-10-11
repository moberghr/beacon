import { describe, it, expect, vi, beforeEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { permissionsForRoles, permissionsHandler } from '@/test/permissions';
import { AiProvider, BedrockAuthMode } from '@/lib/enums';
import AdminSettingsPage from './AdminSettingsPage';

vi.mock('sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

function settings(overrides: Record<string, unknown> = {}) {
  return {
    baseUrl: 'https://beacon.example.test',
    llmProvider: AiProvider.OpenAI,
    llmApiKeySet: true,
    llmEndpointSet: false,
    llmRegion: null,
    llmSessionTokenSet: false,
    llmAwsAccessKeyIdSet: false,
    llmAwsSecretAccessKeySet: false,
    llmBedrockAuthMode: BedrockAuthMode.IamRole,
    llmModel: 'gpt-4o',
    llmFastModel: null,
    llmMaxConcurrentRequests: 4,
    llmTokensPerMinute: 0,
    llmRequestsPerMinute: 0,
    llmMonthlyBudget: 0,
    ...overrides,
  };
}

/** Serves the settings and records what a save and a test send. */
function serve(stored = settings()) {
  const sent = { saves: [] as Record<string, unknown>[], tests: [] as Record<string, unknown>[] };
  mswServer.use(
    http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' })),
    http.get('*/beacon/api/admin-settings', () => HttpResponse.json({ settings: stored, history: [] })),
    http.put('*/beacon/api/admin-settings', async ({ request }) => {
      sent.saves.push((await request.json()) as Record<string, unknown>);
      return new HttpResponse(null, { status: 204 });
    }),
    http.post('*/beacon/api/admin-settings/test-llm', async ({ request }) => {
      sent.tests.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ ok: true, latencyMs: 12, model: 'gpt-4o', error: null, sample: 'ok' });
    }),
  );
  return sent;
}

const apiKeyInput = () => screen.getByPlaceholderText('Set — leave blank to keep');
const providerButton = (name: string) => screen.getByRole('button', { name: new RegExp(`^${name}`) });

describe('AdminSettingsPage permissions', () => {
  beforeEach(() => {
    mswServer.use(permissionsForRoles(['Admin']));
  });

  it('lets an Admin change the LLM connection', async () => {
    serve();
    renderWithProviders(<AdminSettingsPage />);

    expect(await screen.findByText('General')).toBeInTheDocument();
    expect(providerButton('Anthropic')).toBeEnabled();
    expect(apiKeyInput()).toBeEnabled();
    expect(screen.queryByText("The LLM connection is an Admin's to change")).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeInTheDocument();
  });

  it("disables a SecurityAdmin's LLM provider and credentials, and saves the stored connection", async () => {
    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    const sent = serve();
    renderWithProviders(<AdminSettingsPage />);

    expect(await screen.findByText("The LLM connection is an Admin's to change")).toBeInTheDocument();
    expect(providerButton('Anthropic')).toBeDisabled();
    expect(providerButton('OpenAI')).toBeDisabled();
    expect(apiKeyInput()).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /^model/i })).toBeEnabled();

    // The base URL is an Admin's too: a SecurityAdmin saves the stored one back.
    expect(screen.getByPlaceholderText('https://beacon.example.com')).toBeDisabled();
    fireEvent.change(screen.getByRole('spinbutton', { name: /monthly budget/i }), { target: { value: '75' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({
      baseUrl: 'https://beacon.example.test',
      llmMonthlyBudget: 75,
      llmProvider: AiProvider.OpenAI,
      llmRegion: null,
      llmBedrockAuthMode: BedrockAuthMode.IamRole,
      llmModel: 'gpt-4o',
      llmApiKey: null,
      llmEndpoint: null,
      llmSessionToken: null,
      llmAwsAccessKeyId: null,
      llmAwsSecretAccessKey: null,
    });
  });

  it('tests the stored connection for a SecurityAdmin', async () => {
    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    const sent = serve();
    renderWithProviders(<AdminSettingsPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Test connection' }));

    await waitFor(() => expect(sent.tests).toHaveLength(1));
    expect(sent.tests[0]).toMatchObject({ llmProvider: AiProvider.OpenAI, llmApiKey: null, llmEndpoint: null });
    expect(screen.getByText(/with the stored connection/)).toBeInTheDocument();
  });

  it("disables a SecurityAdmin's Bedrock region and authentication mode", async () => {
    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    serve(settings({ llmProvider: AiProvider.Bedrock, llmRegion: 'eu-west-1', llmModel: null }));
    renderWithProviders(<AdminSettingsPage />);

    expect(await screen.findByRole('combobox', { name: /^region/i })).toBeDisabled();
    expect(screen.getByRole('radio', { name: 'Access keys' })).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /^model/i })).toBeEnabled();
  });

  it('shows an Auditor the settings read-only, with no save and no test', async () => {
    mswServer.use(permissionsHandler({ permissions: ['Auditor'] }));
    const sent = serve();
    renderWithProviders(<AdminSettingsPage />);

    expect(await screen.findByText('Read only')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('https://beacon.example.com')).toBeDisabled();
    expect(screen.getByRole('combobox', { name: /^model/i })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Test connection' })).not.toBeInTheDocument();
    expect(sent.saves).toHaveLength(0);
  });
});
