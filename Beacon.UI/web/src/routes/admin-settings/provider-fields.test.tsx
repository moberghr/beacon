import { describe, it, expect } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { useForm } from 'react-hook-form';
import { ModelPickerField, RegionPickerField } from './provider-fields';
import { OPENAI_MODELS } from './queries';
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
