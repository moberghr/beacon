import { describe, expect, it } from 'vitest';
import {
  emptyToNull,
  secretToNull,
  settingsToForm,
  formToUpdatePayload,
  formToTestPayload,
  type AdminSettingsFormValues,
} from './admin-settings-form';
import {
  AiProvider,
  BedrockAuthMode,
  type AdminSettingsView,
} from '../queries';

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
    expect(out.llmApiKey).toBe('');
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
  };

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
    };
    const payload = formToTestPayload(values);
    expect(payload.llmModel).toBe('claude');
    expect(payload.llmEndpoint).toBeNull();
    expect(payload.llmApiKey).toBeNull();
    expect(payload.llmSessionToken).toBe('s');
  });
});
