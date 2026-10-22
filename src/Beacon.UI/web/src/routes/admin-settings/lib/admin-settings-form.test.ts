import { describe, expect, it } from 'vitest';
import {
  emptyToNull,
  secretToNull,
  settingsToForm,
  formToUpdatePayload,
  formToTestPayload,
  keepStoredConnection,
  type AdminSettingsFormValues,
} from './admin-settings-form';
import { AiProvider, BedrockAuthMode } from '@/lib/enums';
import { type AdminSettingsView } from '../queries';

describe('emptyToNull', () => {
  it.each([
    ['', null],
    ['   ', null],
    [undefined, null],
    [null, null],
    ['  hello  ', 'hello'],
    ['kept', 'kept'],
  ])('emptyToNull(%j) → %j', (input, expected) => {
    expect(emptyToNull(input as string | undefined | null)).toBe(expected);
  });
});

describe('secretToNull', () => {
  it.each([
    ['', null],
    [undefined, null],
    [null, null],
    ['  ', '  '], // preserves whitespace — secrets can legit have it
    ['s3cret', 's3cret'],
  ])('secretToNull(%j) → %j', (input, expected) => {
    expect(secretToNull(input as string | undefined | null)).toBe(expected);
  });
});

describe('settingsToForm', () => {
  it('returns sensible defaults when settings are unloaded', () => {
    const out = settingsToForm(undefined);
    expect(out.llmProvider).toBe(AiProvider.OpenAI);
    expect(out.llmBedrockAuthMode).toBe(BedrockAuthMode.IamRole);
    expect(out.llmMaxConcurrentRequests).toBe(5);
    expect(out.llmTokensPerMinute).toBe(0);
    expect(out.llmRateLimitsEnabled).toBe(false);
    expect(out.llmApiKey).toBe('');
  });

  it.each([
    [true, true],
    [false, false],
    [undefined, false],
  ])('reads the stored rate-limit enforcement %j as %j', (stored, expected) => {
    const out = settingsToForm({ ...settingsView(), llmRateLimitsEnabled: stored });
    expect(out.llmRateLimitsEnabled).toBe(expected);
  });

  it('blanks every secret field even when the backend says it is set', () => {
    const settings: AdminSettingsView = {
      baseUrl: 'https://b',
      llmProvider: AiProvider.Bedrock,
      llmApiKeySet: true,
      llmEndpointSet: true,
      llmRegion: 'eu-west-1',
      llmSessionTokenSet: true,
      llmAwsAccessKeyIdSet: true,
      llmAwsSecretAccessKeySet: true,
      llmBedrockAuthMode: BedrockAuthMode.AccessKey,
      llmModel: 'claude-opus-4-7',
      llmFastModel: null,
      llmMaxConcurrentRequests: 10,
      llmTokensPerMinute: 100,
      llmRequestsPerMinute: 60,
      llmMonthlyBudget: 200,
    };
    const out = settingsToForm(settings);
    expect(out.llmApiKey).toBe('');
    expect(out.llmEndpoint).toBe('');
    expect(out.llmSessionToken).toBe('');
    expect(out.llmAwsAccessKeyId).toBe('');
    expect(out.llmAwsSecretAccessKey).toBe('');
    // Non-secret fields are copied through
    expect(out.llmProvider).toBe(AiProvider.Bedrock);
    expect(out.llmModel).toBe('claude-opus-4-7');
    expect(out.llmRegion).toBe('eu-west-1');
  });
});

describe('formToUpdatePayload', () => {
  const base: AdminSettingsFormValues = {
    baseUrl: '  https://b  ',
    llmProvider: AiProvider.OpenAI,
    llmModel: 'gpt-4o',
    llmFastModel: '',
    llmRegion: '   ',
    llmBedrockAuthMode: BedrockAuthMode.IamRole,
    llmApiKey: '',
    llmEndpoint: '',
    llmSessionToken: '',
    llmAwsAccessKeyId: '',
    llmAwsSecretAccessKey: '',
    llmMaxConcurrentRequests: 5,
    llmTokensPerMinute: 0,
    llmRequestsPerMinute: 0,
    llmMonthlyBudget: 0,
    llmRateLimitsEnabled: false,
  };

  it('carries whether the rate limits are enforced', () => {
    expect(formToUpdatePayload({ ...base, llmRateLimitsEnabled: true }).llmRateLimitsEnabled).toBe(true);
    expect(formToUpdatePayload(base).llmRateLimitsEnabled).toBe(false);
  });

  it('trims non-secret fields and nulls empty/whitespace ones', () => {
    const payload = formToUpdatePayload(base);
    expect(payload.baseUrl).toBe('https://b');
    expect(payload.llmFastModel).toBeNull();
    expect(payload.llmRegion).toBeNull();
  });

  it('routes blank secrets to null (= leave unchanged)', () => {
    const payload = formToUpdatePayload(base);
    expect(payload.llmApiKey).toBeNull();
    expect(payload.llmEndpoint).toBeNull();
    expect(payload.llmAwsSecretAccessKey).toBeNull();
  });

  it('passes filled secrets through verbatim (no trim)', () => {
    const payload = formToUpdatePayload({ ...base, llmApiKey: '  s3cret  ' });
    expect(payload.llmApiKey).toBe('  s3cret  ');
  });
});

describe('formToTestPayload', () => {
  it('uses the same null/trim rules as formToUpdatePayload', () => {
    const values: AdminSettingsFormValues = {
      baseUrl: 'irrelevant',
      llmProvider: AiProvider.Bedrock,
      llmModel: '  claude  ',
      llmFastModel: 'irrelevant',
      llmRegion: 'eu-west-1',
      llmBedrockAuthMode: BedrockAuthMode.IamRole,
      llmApiKey: '',
      llmEndpoint: '   ',
      llmSessionToken: 's',
      llmAwsAccessKeyId: '',
      llmAwsSecretAccessKey: '',
      llmMaxConcurrentRequests: 1,
      llmTokensPerMinute: 0,
      llmRequestsPerMinute: 0,
      llmMonthlyBudget: 0,
      llmRateLimitsEnabled: false,
    };
    const payload = formToTestPayload(values);
    expect(payload.llmModel).toBe('claude');
    expect(payload.llmEndpoint).toBeNull();
    expect(payload.llmApiKey).toBeNull();
    expect(payload.llmSessionToken).toBe('s');
  });
});

describe('keepStoredConnection', () => {
  const stored = {
    baseUrl: null,
    llmProvider: AiProvider.Bedrock,
    llmApiKeySet: false,
    llmEndpointSet: false,
    llmRegion: 'eu-west-1',
    llmSessionTokenSet: false,
    llmAwsAccessKeyIdSet: true,
    llmAwsSecretAccessKeySet: true,
    llmBedrockAuthMode: BedrockAuthMode.AccessKey,
    llmModel: 'anthropic.claude',
    llmFastModel: null,
    llmMaxConcurrentRequests: 4,
    llmTokensPerMinute: 0,
    llmRequestsPerMinute: 0,
    llmMonthlyBudget: 0,
  } satisfies AdminSettingsView;

  it('sends the stored provider, region and authentication mode, and no endpoint or credential', () => {
    const edited = formToUpdatePayload({
      ...settingsToForm(stored),
      llmProvider: AiProvider.OpenAI,
      llmRegion: 'us-east-1',
      llmBedrockAuthMode: BedrockAuthMode.IamRole,
      llmApiKey: 'sk-new',
      llmEndpoint: 'https://elsewhere.example.test',
      llmAwsAccessKeyId: 'AKIA',
      llmModel: 'anthropic.claude-v2',
      llmMonthlyBudget: 50,
    });

    const payload = keepStoredConnection(edited, stored);

    expect(payload).toMatchObject({
      llmProvider: AiProvider.Bedrock,
      llmRegion: 'eu-west-1',
      llmBedrockAuthMode: BedrockAuthMode.AccessKey,
      llmApiKey: null,
      llmEndpoint: null,
      llmSessionToken: null,
      llmAwsAccessKeyId: null,
      llmAwsSecretAccessKey: null,
      // The rest is the form's.
      llmModel: 'anthropic.claude-v2',
      llmMonthlyBudget: 50,
    });
  });

  it('keeps no provider when none is stored', () => {
    const payload = keepStoredConnection(formToTestPayload(settingsToForm(undefined)), { ...stored, llmProvider: null, llmRegion: null });

    expect(payload.llmProvider).toBeNull();
    expect(payload.llmRegion).toBeNull();
  });
});

function settingsView(): AdminSettingsView {
  return {
    baseUrl: null,
    llmProvider: AiProvider.OpenAI,
    llmApiKeySet: false,
    llmEndpointSet: false,
    llmRegion: null,
    llmSessionTokenSet: false,
    llmAwsAccessKeyIdSet: false,
    llmAwsSecretAccessKeySet: false,
    llmBedrockAuthMode: BedrockAuthMode.IamRole,
    llmModel: null,
    llmFastModel: null,
    llmMaxConcurrentRequests: 4,
    llmTokensPerMinute: 0,
    llmRequestsPerMinute: 0,
    llmMonthlyBudget: 0,
  };
}
