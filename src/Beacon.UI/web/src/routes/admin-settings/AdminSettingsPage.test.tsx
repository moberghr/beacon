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

/** The form takes the stored settings in an effect after its fields appear: an edit made before that is reset. */
const storedSettingsApplied = () =>
  waitFor(() => expect(screen.getByPlaceholderText('https://beacon.example.com')).toHaveValue('https://beacon.example.test'));

/** Saves once Save is enabled: it waits for the installed providers to load. */
async function save() {
  const button = screen.getByRole('button', { name: 'Save changes' });
  await waitFor(() => expect(button).toBeEnabled());
  fireEvent.click(button);
}

const llmProviders = (resolver: Parameters<typeof http.get>[1]) => http.get('*/beacon/api/admin-settings/llm-providers', resolver);

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
    // So are the concurrency and per-minute limits and their enforcement: the stored ones are sent back.
    expect(screen.getByRole('spinbutton', { name: /max concurrent requests/i })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: 'Enforce requests/tokens per minute' })).toBeDisabled();
    expect(screen.getByPlaceholderText('https://beacon.example.com')).toBeDisabled();
    await storedSettingsApplied();
    fireEvent.change(screen.getByRole('spinbutton', { name: /monthly budget/i }), { target: { value: '75' } });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({
      baseUrl: 'https://beacon.example.test',
      llmMonthlyBudget: 75,
      llmMaxConcurrentRequests: 4,
      llmRateLimitsEnabled: false,
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

describe('AdminSettingsPage API key', () => {
  beforeEach(() => {
    mswServer.use(permissionsForRoles(['Admin']));
  });

  const changeBudget = async () => {
    await storedSettingsApplied();
    fireEvent.change(screen.getByRole('spinbutton', { name: /monthly budget/i }), { target: { value: '75' } });
  };

  it('saves other settings while no API key is stored for the kept provider', async () => {
    const sent = serve(settings({ llmApiKeySet: false }));
    renderWithProviders(<AdminSettingsPage />);

    // changeBudget waits for the stored settings, so the form reset cannot overwrite the base URL edit.
    await changeBudget();
    fireEvent.change(screen.getByPlaceholderText('https://beacon.example.com'), {
      target: { value: 'https://beacon.example.org' },
    });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ baseUrl: 'https://beacon.example.org', llmApiKey: null, llmMonthlyBudget: 75 });
    expect(screen.queryByText('This provider needs an API key.')).not.toBeInTheDocument();
  });

  it('saves other settings while no provider is stored and the picker is left on its default', async () => {
    const sent = serve(settings({ llmProvider: null, llmApiKeySet: false }));
    renderWithProviders(<AdminSettingsPage />);

    await changeBudget();
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.OpenAI, llmApiKey: null, llmMonthlyBudget: 75 });
    expect(screen.queryByText('This provider needs an API key.')).not.toBeInTheDocument();
  });

  it('saves with the stored key when the provider is kept', async () => {
    const sent = serve();
    renderWithProviders(<AdminSettingsPage />);

    await changeBudget();
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.OpenAI, llmApiKey: null, llmMonthlyBudget: 75 });
  });

  it('sends the key the Admin typed when none is stored', async () => {
    const sent = serve(settings({ llmApiKeySet: false }));
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    fireEvent.change(screen.getByPlaceholderText('sk-…'), { target: { value: 'sk-typed' } });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.OpenAI, llmApiKey: 'sk-typed' });
  });

  it("does not hold a SecurityAdmin's save on the API key", async () => {
    mswServer.use(permissionsHandler({ permissions: ['SecurityAdmin'] }));
    const sent = serve(settings({ llmApiKeySet: false }));
    renderWithProviders(<AdminSettingsPage />);

    await changeBudget();
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.OpenAI, llmApiKey: null, llmMonthlyBudget: 75 });
  });

  it('asks for a new API key when the provider changes, though a key is stored for the previous one', async () => {
    const sent = serve();
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    fireEvent.click(providerButton('Anthropic'));
    await save();

    expect(await screen.findByText('This provider needs an API key.')).toBeInTheDocument();
    expect(sent.saves).toHaveLength(0);

    fireEvent.change(screen.getByPlaceholderText('sk-ant-…'), { target: { value: 'sk-ant-typed' } });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.Claude, llmApiKey: 'sk-ant-typed' });
  });

  it('saves a provider that needs no API key without one', async () => {
    const sent = serve(settings({ llmProvider: AiProvider.Bedrock, llmApiKeySet: false, llmRegion: 'eu-west-1' }));
    renderWithProviders(<AdminSettingsPage />);

    await changeBudget();
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmProvider: AiProvider.Bedrock, llmApiKey: null });
  });
});

describe('AdminSettingsPage installed providers', () => {
  beforeEach(() => {
    mswServer.use(permissionsForRoles(['Admin']));
  });

  it('shows a stored provider that is not installed disabled, and offers the installed ones', async () => {
    mswServer.use(
      llmProviders(() =>
        HttpResponse.json({
          aiEnabled: true,
          providers: [
            { provider: AiProvider.OpenAI, requiresApiKey: true },
            { provider: AiProvider.Claude, requiresApiKey: true },
            { provider: AiProvider.AzureOpenAI, requiresApiKey: true },
          ],
        }),
      ),
    );
    serve(settings({ llmProvider: AiProvider.Bedrock, llmApiKeySet: false, llmRegion: 'eu-west-1' }));
    renderWithProviders(<AdminSettingsPage />);

    await waitFor(() => expect(providerButton('AWS Bedrock')).toBeDisabled());
    expect(providerButton('AWS Bedrock')).toHaveTextContent('Package not installed');
    expect(providerButton('Anthropic')).toBeEnabled();
  });

  it('holds Save until the installed providers are known, but not the connection test', async () => {
    let release!: () => void;
    const loaded = new Promise<void>(resolve => {
      release = resolve;
    });
    mswServer.use(
      llmProviders(async () => {
        await loaded;
        return HttpResponse.json({ aiEnabled: true, providers: [{ provider: AiProvider.OpenAI, requiresApiKey: true }] });
      }),
    );
    serve();
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    fireEvent.change(screen.getByRole('spinbutton', { name: /monthly budget/i }), { target: { value: '75' } });

    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeEnabled();

    release();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled());
  });

  it('warns when the installed providers cannot be loaded, offers every provider, and asks for a key on a switch', async () => {
    mswServer.use(llmProviders(() => new HttpResponse(null, { status: 500 })));
    const sent = serve(settings({ llmApiKeySet: false }));
    renderWithProviders(<AdminSettingsPage />);

    expect(await screen.findByText('The installed providers could not be loaded')).toBeInTheDocument();
    for (const name of ['OpenAI', 'Anthropic', 'Azure OpenAI', 'AWS Bedrock']) {
      expect(providerButton(name)).toBeEnabled();
    }

    await storedSettingsApplied();
    fireEvent.click(providerButton('Anthropic'));
    await save();

    expect(await screen.findByText('This provider needs an API key.')).toBeInTheDocument();
    expect(sent.saves).toHaveLength(0);
  });
});

describe('AdminSettingsPage rate limits', () => {
  beforeEach(() => {
    mswServer.use(permissionsForRoles(['Admin']));
  });

  const enforce = () => screen.getByRole('checkbox', { name: 'Enforce requests/tokens per minute' });
  const requestsPerMinute = () => screen.getByRole('spinbutton', { name: /requests per minute/i });
  const tokensPerMinute = () => screen.getByRole('spinbutton', { name: /tokens per minute/i });

  it('disables the per-minute limits while they are not enforced, and says the budget is not enforced', async () => {
    serve(settings({ llmRequestsPerMinute: 60, llmTokensPerMinute: 1000 }));
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    expect(enforce()).not.toBeChecked();
    expect(requestsPerMinute()).toBeDisabled();
    expect(tokensPerMinute()).toBeDisabled();
    expect(screen.getAllByText('Recorded, not enforced: turn on enforcement to apply it.')).toHaveLength(2);
    expect(screen.getByText(/Recorded, not enforced: Beacon does not track spend/)).toBeInTheDocument();
  });

  it('enables the per-minute limits once enforcement is turned on, and saves the flag with them', async () => {
    const sent = serve(settings({ llmRequestsPerMinute: 60, llmTokensPerMinute: 1000 }));
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    fireEvent.click(enforce());
    await waitFor(() => expect(requestsPerMinute()).toBeEnabled());
    expect(tokensPerMinute()).toBeEnabled();
    fireEvent.change(requestsPerMinute(), { target: { value: '30' } });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmRateLimitsEnabled: true, llmRequestsPerMinute: 30, llmTokensPerMinute: 1000 });
  });

  it('keeps the stored per-minute limits when saving with enforcement off', async () => {
    const sent = serve(settings({ llmRequestsPerMinute: 60, llmTokensPerMinute: 1000 }));
    renderWithProviders(<AdminSettingsPage />);

    await storedSettingsApplied();
    fireEvent.change(screen.getByRole('spinbutton', { name: /monthly budget/i }), { target: { value: '75' } });
    await save();

    await waitFor(() => expect(sent.saves).toHaveLength(1));
    expect(sent.saves[0]).toMatchObject({ llmRateLimitsEnabled: false, llmRequestsPerMinute: 60, llmTokensPerMinute: 1000 });
  });

  it('shows the stored enforcement', async () => {
    serve(settings({ llmRateLimitsEnabled: true }));
    renderWithProviders(<AdminSettingsPage />);

    await waitFor(() => expect(enforce()).toBeChecked());
    expect(requestsPerMinute()).toBeEnabled();
  });
});
