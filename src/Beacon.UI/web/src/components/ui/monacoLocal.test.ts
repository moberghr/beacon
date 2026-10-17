import { afterEach, describe, expect, it, vi } from 'vitest';

vi.mock('monaco-editor/esm/vs/editor/editor.api', () => ({ editor: {}, languages: {} }));
vi.mock('monaco-editor/esm/vs/editor/editor.all', () => ({}));
vi.mock('monaco-editor/esm/vs/basic-languages/sql/sql.contribution', () => ({}));
vi.mock('monaco-editor/esm/vs/editor/editor.worker?worker', () => ({
  default: class FakeWorker {},
}));

describe('loadLocalMonaco', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.resetModules();
  });

  it('configures the bundled editor without touching the network', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch');
    const { loadLocalMonaco } = await import('./monacoLocal');

    const monaco = await loadLocalMonaco();

    expect(monaco).toHaveProperty('languages');
    const env = (self as unknown as { MonacoEnvironment: { getWorker: () => unknown } })
      .MonacoEnvironment;
    expect(env.getWorker()).toBeDefined();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('rejects instead of falling back when the bundled editor cannot load', async () => {
    vi.doMock('monaco-editor/esm/vs/editor/editor.all', () => {
      throw new Error('bundle missing');
    });
    const { loadLocalMonaco } = await import('./monacoLocal');

    await expect(loadLocalMonaco()).rejects.toThrow();
  });
});
