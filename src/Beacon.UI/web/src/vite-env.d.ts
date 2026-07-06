/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Short git SHA injected at build time by vite.config.ts. */
  readonly BEACON_COMMIT_SHA: string;
  /** ISO-8601 build timestamp injected at build time by vite.config.ts. */
  readonly BEACON_BUILD_DATE: string;
  /** '1' enables the MSW mocked-API dev mode (`npm run dev:mock`). Dev only. */
  readonly VITE_MOCK_API?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
