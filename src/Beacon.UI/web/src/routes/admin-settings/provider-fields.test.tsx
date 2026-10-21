import { describe, it, expect } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { useForm } from 'react-hook-form';
import { AiProvider } from '@/lib/enums';
import { ModelPickerField, ProviderPicker, RegionPickerField } from './provider-fields';
import { OPENAI_MODELS, providerRequiresApiKey, type LlmProvidersResult } from './queries';
import { settingsToForm, type FormValues } from './lib/admin-settings-form';
import type { UseFormReturn } from 'react-hook-form';

let formApi: UseFormReturn<FormValues>;

function ModelHarness() {
  const form = useForm<FormValues>({ defaultValues: settingsToForm(undefined) });
  formApi = form;
  return (
    <ModelPickerField
      label="Model"
      options={OPENAI_MODELS}
      registerName="llmModel"
      register={form.register}
      setValue={form.setValue}
      control={form.control}
    />
  );
}

function RegionHarness() {
  const form = useForm<FormValues>({ defaultValues: settingsToForm(undefined) });
  formApi = form;
  return (
    <RegionPickerField
      register={form.register}
      setValue={form.setValue}
      control={form.control}
    />
  );
}

describe('ModelPickerField', () => {
  it('shows an explicit placeholder while the form value is empty (display === form value)', () => {
    render(<ModelHarness />);
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    expect(select.value).toBe('');
    expect(formApi.getValues('llmModel')).toBe('');
    expect(screen.getByText('Select a model…')).toBeTruthy();
  });

  it('writes the picked preset into the form so display and saved value match', () => {
    render(<ModelHarness />);
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'gpt-4o-mini' } });
    expect(formApi.getValues('llmModel')).toBe('gpt-4o-mini');
    expect(select.value).toBe('gpt-4o-mini');
  });

  it('reflects form.reset() — the select displays the loaded settings value', () => {
    render(<ModelHarness />);
    act(() => {
      formApi.reset({ ...settingsToForm(undefined), llmModel: 'gpt-4-turbo' });
    });
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    expect(select.value).toBe('gpt-4-turbo');
  });

  it('flips to the custom input when reset puts a non-preset value in the form', () => {
    render(<ModelHarness />);
    act(() => {
      formApi.reset({ ...settingsToForm(undefined), llmModel: 'my-private-model' });
    });
    const input = screen.getByPlaceholderText('Custom model id') as HTMLInputElement;
    expect(input.value).toBe('my-private-model');
  });
});

describe('RegionPickerField', () => {
  it('never displays a region the form does not hold', () => {
    render(<RegionHarness />);
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    // Unset region → placeholder, not the first preset.
    expect(select.value).toBe('');
    expect(formApi.getValues('llmRegion')).toBe('');

    fireEvent.change(select, { target: { value: 'eu-central-1' } });
    expect(formApi.getValues('llmRegion')).toBe('eu-central-1');
    expect(select.value).toBe('eu-central-1');
  });
});

const BUILT_INS: LlmProvidersResult = {
  aiEnabled: true,
  providers: [
    { provider: AiProvider.OpenAI, requiresApiKey: true },
    { provider: AiProvider.Claude, requiresApiKey: true },
    { provider: AiProvider.AzureOpenAI, requiresApiKey: true },
  ],
};

const providerButton = (name: string) => screen.queryByRole('button', { name: new RegExp(`^${name}`) });

describe('ProviderPicker', () => {
  it('offers only the providers the host installed', () => {
    render(<ProviderPicker value={AiProvider.OpenAI} onChange={() => {}} catalog={BUILT_INS} storedProvider={AiProvider.OpenAI} />);

    expect(providerButton('OpenAI')).toBeEnabled();
    expect(providerButton('Anthropic')).toBeEnabled();
    expect(providerButton('Azure OpenAI')).toBeEnabled();
    expect(providerButton('AWS Bedrock')).toBeNull();
    expect(screen.queryByText('Package not installed')).toBeNull();
  });

  it('shows a stored provider that is not installed as selected but disabled, and lets another be picked', () => {
    const picked: AiProvider[] = [];
    render(<ProviderPicker value={AiProvider.Bedrock} onChange={x => picked.push(x)} catalog={BUILT_INS} storedProvider={AiProvider.Bedrock} />);

    const bedrock = providerButton('AWS Bedrock');
    expect(bedrock).toBeDisabled();
    expect(bedrock).toHaveAttribute('aria-pressed', 'true');
    expect(bedrock).toHaveTextContent('Package not installed');

    fireEvent.click(providerButton('Anthropic')!);
    expect(picked).toEqual([AiProvider.Claude]);
  });

  it('offers all four while AI is off', () => {
    render(
      <ProviderPicker
        value={AiProvider.OpenAI}
        onChange={() => {}}
        catalog={{ aiEnabled: false, providers: [] }}
        storedProvider={null}
      />,
    );

    for (const name of ['OpenAI', 'Anthropic', 'Azure OpenAI', 'AWS Bedrock']) {
      expect(providerButton(name)).toBeEnabled();
    }
    expect(screen.queryByText('Package not installed')).toBeNull();
  });
});

describe('providerRequiresApiKey', () => {
  it("follows the host's requiresApiKey, and requires nothing while AI is off", () => {
    const all: LlmProvidersResult = {
      aiEnabled: true,
      providers: [...BUILT_INS.providers, { provider: AiProvider.Bedrock, requiresApiKey: false }],
    };

    expect(providerRequiresApiKey(all, AiProvider.OpenAI)).toBe(true);
    expect(providerRequiresApiKey(all, AiProvider.Bedrock)).toBe(false);
    expect(providerRequiresApiKey({ aiEnabled: false, providers: [] }, AiProvider.OpenAI)).toBe(false);
  });

  it('requires a key when unknown, for a provider whose form has an API key field', () => {
    expect(providerRequiresApiKey(undefined, AiProvider.OpenAI)).toBe(true);
    expect(providerRequiresApiKey(undefined, AiProvider.AzureOpenAI)).toBe(true);
    expect(providerRequiresApiKey(undefined, AiProvider.Bedrock)).toBe(false);
    expect(providerRequiresApiKey({ aiEnabled: true, providers: [] }, AiProvider.Claude)).toBe(true);
  });
});
