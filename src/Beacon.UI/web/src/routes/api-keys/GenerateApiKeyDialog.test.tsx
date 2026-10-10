import { describe, it, expect, beforeEach } from 'vitest';
import { http, HttpResponse } from 'msw';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { focusManager } from '@tanstack/react-query';
import { mswServer } from '../../../vitest.setup';
import { renderWithProviders } from '@/test/render';
import { GenerateApiKeyDialog } from './GenerateApiKeyDialog';

const csrf = http.get('*/beacon/api/csrf', () => HttpResponse.json({ token: 'test-token' }));

const permissions = (canWrite: boolean) =>
  http.get('*/beacon/api/auth/permissions', () => HttpResponse.json({ canRead: true, canWrite }));

/** Records every create request; answers with a key shown once. */
function recordCreates() {
  const payloads: unknown[] = [];
  mswServer.use(
    csrf,
    http.post('*/beacon/api/api-keys', async ({ request }) => {
      payloads.push(await request.json());
      return HttpResponse.json({ plainTextKey: 'sk-sem_shown-once' });
    }),
  );
  return payloads;
}

// The scope checkboxes sit inside the Scopes field's label too, so their accessible names overlap: find each by its
// own label's text.
const scopeCheckbox = (text: RegExp) =>
  screen.getByText(text).closest('label')!.querySelector('input') as HTMLInputElement;

const typeName = (value: string) =>
  fireEvent.input(screen.getByPlaceholderText('e.g. CI Pipeline'), { target: { value } });

const submit = () => fireEvent.click(screen.getByRole('button', { name: 'Generate key' }));

describe('GenerateApiKeyDialog', () => {
  beforeEach(cleanup);

  it("submits a Viewer's key as Read only", async () => {
    mswServer.use(permissions(false));
    const payloads = recordCreates();
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);
    await screen.findByText(/requires the Editor role/);

    typeName('Reporting');
    submit();

    expect(await screen.findByText('sk-sem_shown-once')).toBeInTheDocument();
    expect(payloads).toHaveLength(1);
    expect(payloads[0]).toMatchObject({ name: 'Reporting', scopes: ['Read'], allowedProjectIds: null, expiresAt: null });
  });

  it("submits a writer's Read and Execute key", async () => {
    mswServer.use(permissions(true));
    const payloads = recordCreates();
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);
    await waitFor(() => expect(scopeCheckbox(/^Execute$/)).toBeEnabled());

    typeName('Pipeline');
    fireEvent.click(scopeCheckbox(/^Execute$/));
    submit();

    await screen.findByText('sk-sem_shown-once');
    expect(payloads[0]).toMatchObject({ name: 'Pipeline', scopes: ['Read', 'Execute'] });
  });

  it('says why a requested Execute key cannot be issued instead of creating a Read key', async () => {
    let canWrite = true;
    mswServer.use(http.get('*/beacon/api/auth/permissions', () => HttpResponse.json({ canRead: true, canWrite })));
    const payloads = recordCreates();
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);
    await waitFor(() => expect(scopeCheckbox(/^Execute$/)).toBeEnabled());
    typeName('Pipeline');
    fireEvent.click(scopeCheckbox(/^Execute$/));

    // The permissions change while the dialog is open (refetched on focus).
    canWrite = false;
    act(() => {
      focusManager.setFocused(false);
      focusManager.setFocused(true);
    });
    await screen.findByText(/requires the Editor role/);
    focusManager.setFocused(undefined);
    expect(scopeCheckbox(/^Execute$/)).toBeEnabled();
    submit();

    expect(await screen.findByText(/Execute keys need the Editor role/)).toBeInTheDocument();
    expect(payloads).toHaveLength(0);
  });

  it('refuses to submit without a scope and says why', async () => {
    mswServer.use(permissions(true));
    const payloads = recordCreates();
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);
    await waitFor(() => expect(scopeCheckbox(/^Execute$/)).toBeEnabled());

    typeName('Nothing');
    fireEvent.click(scopeCheckbox(/^Read$/));
    submit();

    expect(await screen.findByText('Pick at least one scope')).toBeInTheDocument();
    expect(payloads).toHaveLength(0);
  });

  it('keeps Execute disabled while the permissions load', async () => {
    let release!: () => void;
    const pending = new Promise<void>(resolve => {
      release = resolve;
    });
    mswServer.use(
      http.get('*/beacon/api/auth/permissions', async () => {
        await pending;
        return HttpResponse.json({ canRead: true, canWrite: true });
      }),
    );
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);

    expect(await screen.findByText(/checking your permissions/)).toBeInTheDocument();
    expect(scopeCheckbox(/^Execute$/)).toBeDisabled();

    release();
    await waitFor(() => expect(scopeCheckbox(/^Execute$/)).toBeEnabled());
    expect(screen.queryByText(/checking your permissions/)).not.toBeInTheDocument();
  });

  it('keeps Execute disabled when the permissions cannot be loaded', async () => {
    mswServer.use(http.get('*/beacon/api/auth/permissions', () => new HttpResponse(null, { status: 500 })));
    renderWithProviders(<GenerateApiKeyDialog open onClose={() => {}} />);

    expect(await screen.findByText(/could not check your permissions/)).toBeInTheDocument();
    expect(scopeCheckbox(/^Execute$/)).toBeDisabled();
  });
});
