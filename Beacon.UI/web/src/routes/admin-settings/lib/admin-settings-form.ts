import { z } from 'zod';
import {
  AiProvider,
  BedrockAuthMode,
  type AdminSettingsView,
  type AiProviderId,
  type BedrockAuthModeId,
  type UpdateAdminSettingsPayload,
  type TestLlmConnectionPayload,
} from '../queries';

/**
 * Schema source of truth for the admin-settings form. Lives next to the
 * form helpers so both the page and any extracted sub-components share
 * the same `FormValues` type.
 */
export const ADMIN_SETTINGS_SCHEMA = z.object({
  baseUrl: z.string().trim().max(500).optional(),
  llmProvider: z.number().int().min(0).max(3),
  llmModel: z.string().trim().max(200).optional(),
  llmFastModel: z.string().trim().max(200).optional(),
  llmRegion: z.string().trim().max(100).optional(),
  llmBedrockAuthMode: z.number().int().min(0).max(2),
  llmApiKey: z.string().max(500).optional(),
  llmEndpoint: z.string().trim().max(500).optional(),
  llmSessionToken: z.string().max(2000).optional(),
  llmAwsAccessKeyId: z.string().max(200).optional(),
  llmAwsSecretAccessKey: z.string().max(500).optional(),
  llmMaxConcurrentRequests: z.number().int().min(1).max(1000),
  llmTokensPerMinute: z.number().int().min(0),
  llmRequestsPerMinute: z.number().int().min(0),
  llmMonthlyBudget: z.number().min(0),
});

export type FormValues = z.infer<typeof ADMIN_SETTINGS_SCHEMA>;

/**
 * Form shape used by `AdminSettingsForm`. Mirrors `AdminSettingsView` but
 * with strings for every field (RHF + zod prefer plain strings; `null`
 * round-trips through the conversion helpers below).
 */
/**
 * Alias kept for callers that import this name. The canonical type is
 * `FormValues` (above), inferred from `ADMIN_SETTINGS_SCHEMA`.
 */
export type AdminSettingsFormValues = FormValues;

/**
 * Trim and treat empty strings as `null`. Used for non-secret string
 * fields where the backend distinguishes "unset" from "empty".
 */
export function emptyToNull(value: string | undefined | null): string | null {
  if (value === undefined || value === null) return null;
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Like `emptyToNull` but does NOT trim — secret fields can legitimately
 * contain leading/trailing whitespace and the backend treats `null` as
 * "leave unchanged" rather than "clear".
 */
export function secretToNull(value: string | undefined | null): string | null {
  if (value === undefined || value === null) return null;
  return value.length === 0 ? null : value;
}

/**
 * Pull the loaded `AdminSettingsView` (or the unloaded default) into the
 * form shape. Secret fields are always blanked on load — the backend
 * only tells us whether one is set, never the value.
 */
export function settingsToForm(s: AdminSettingsView | undefined): AdminSettingsFormValues {
  if (s === undefined) {
    return {
      baseUrl: '',
      llmProvider: AiProvider.OpenAI,
      llmModel: '',
      llmFastModel: '',
      llmRegion: '',
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
  }
  return {
    baseUrl: s.baseUrl ?? '',
    llmProvider: (s.llmProvider ?? AiProvider.OpenAI) as AiProviderId,
    llmModel: s.llmModel ?? '',
    llmFastModel: s.llmFastModel ?? '',
    llmRegion: s.llmRegion ?? '',
    llmBedrockAuthMode: s.llmBedrockAuthMode,
    llmApiKey: '',
    llmEndpoint: '',
    llmSessionToken: '',
    llmAwsAccessKeyId: '',
    llmAwsSecretAccessKey: '',
    llmMaxConcurrentRequests: s.llmMaxConcurrentRequests,
    llmTokensPerMinute: s.llmTokensPerMinute,
    llmRequestsPerMinute: s.llmRequestsPerMinute,
    llmMonthlyBudget: s.llmMonthlyBudget,
  };
}

/**
 * Convert form values to the update-payload shape the backend expects.
 */
export function formToUpdatePayload(values: AdminSettingsFormValues): UpdateAdminSettingsPayload {
  return {
    baseUrl: emptyToNull(values.baseUrl),
    llmProvider: values.llmProvider as AiProviderId,
    llmModel: emptyToNull(values.llmModel),
    llmFastModel: emptyToNull(values.llmFastModel),
    llmRegion: emptyToNull(values.llmRegion),
    llmBedrockAuthMode: values.llmBedrockAuthMode as BedrockAuthModeId,
    llmApiKey: secretToNull(values.llmApiKey),
    llmEndpoint: emptyToNull(values.llmEndpoint),
    llmSessionToken: secretToNull(values.llmSessionToken),
    llmAwsAccessKeyId: secretToNull(values.llmAwsAccessKeyId),
    llmAwsSecretAccessKey: secretToNull(values.llmAwsSecretAccessKey),
    llmMaxConcurrentRequests: values.llmMaxConcurrentRequests,
    llmTokensPerMinute: values.llmTokensPerMinute,
    llmRequestsPerMinute: values.llmRequestsPerMinute,
    llmMonthlyBudget: values.llmMonthlyBudget,
  };
}

/**
 * Convert form values to the test-connection payload shape (subset of
 * the update payload — only fields the connection test needs).
 */
export function formToTestPayload(values: AdminSettingsFormValues): TestLlmConnectionPayload {
  return {
    llmProvider: values.llmProvider as AiProviderId,
    llmModel: emptyToNull(values.llmModel),
    llmRegion: emptyToNull(values.llmRegion),
    llmBedrockAuthMode: values.llmBedrockAuthMode as BedrockAuthModeId,
    llmApiKey: secretToNull(values.llmApiKey),
    llmEndpoint: emptyToNull(values.llmEndpoint),
    llmSessionToken: secretToNull(values.llmSessionToken),
    llmAwsAccessKeyId: secretToNull(values.llmAwsAccessKeyId),
    llmAwsSecretAccessKey: secretToNull(values.llmAwsSecretAccessKey),
  };
}
