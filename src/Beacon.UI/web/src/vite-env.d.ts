/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** '1' enables the MSW mocked-API dev mode (`npm run dev:mock`). Dev only. */
  readonly VITE_MOCK_API?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

// Side-effect entry points of monaco-editor; the package ships no typings for them.
declare module 'monaco-editor/esm/vs/editor/editor.all';
declare module 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution';
declare module 'monaco-editor/esm/vs/editor/editor.api';
