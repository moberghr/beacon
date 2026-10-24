import type { loader } from '@monaco-editor/react';

type MonacoNamespace = Parameters<typeof loader.config>[0]['monaco'];

// The app is self-hosted and must not load code from a CDN, so @monaco-editor/react is pointed at
// a locally bundled Monaco. Only the editor core and the SQL language are imported: the generic
// 'monaco-editor' entry also pulls in the ts/css/html/json language services and their workers,
// none of which this editor uses. A failure here rejects; there is deliberately no CDN fallback.
export async function loadLocalMonaco(): Promise<MonacoNamespace> {
  const [monaco, , , editorWorkerMod] = await Promise.all([
    import('monaco-editor/editor/editor.api'),
    import('monaco-editor/features/register.all'),
    import('monaco-editor/languages/definitions/sql/register'),
    import('monaco-editor/editor/editor.worker?worker'),
  ]);
  const EditorWorker = (editorWorkerMod as { default: new () => Worker }).default;
  (self as unknown as { MonacoEnvironment: { getWorker: () => Worker } }).MonacoEnvironment = {
    getWorker: () => new EditorWorker(),
  };
  return monaco as unknown as MonacoNamespace;
}
