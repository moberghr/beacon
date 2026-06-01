import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import { execSync } from 'node:child_process';

const backend = process.env.BEACON_BACKEND_URL ?? 'https://localhost:7187';

// Build-time identity: short git SHA + ISO build timestamp.
// Surfaced via `import.meta.env.BEACON_COMMIT_SHA` / `BEACON_BUILD_DATE`
// so the UI can render a single source of truth for the build (Sidebar + login).
function readGitSha(): string {
  try {
    return execSync('git rev-parse --short HEAD', { stdio: ['ignore', 'pipe', 'ignore'] })
      .toString()
      .trim();
  } catch {
    return 'unknown';
  }
}

const commitSha = process.env.BEACON_COMMIT_SHA ?? readGitSha();
const buildDate = process.env.BEACON_BUILD_DATE ?? new Date().toISOString();

export default defineConfig({
  base: '/',
  plugins: [react()],
  define: {
    'import.meta.env.BEACON_COMMIT_SHA': JSON.stringify(commitSha),
    'import.meta.env.BEACON_BUILD_DATE': JSON.stringify(buildDate),
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  // Output lands in Beacon.UI/wwwroot — the Razor Class Library ships this as
  // static web assets at the root path (see StaticWebAssetBasePath in the csproj).
  // Consumers serve them via app.MapBeaconUi().
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/beacon/api': {
        target: backend,
        secure: false,
        changeOrigin: true,
        ws: true,
      },
      '/beacon/mcp': {
        target: backend,
        secure: false,
        changeOrigin: true,
        ws: true,
      },
      '/hangfire': {
        target: backend,
        secure: false,
        changeOrigin: true,
      },
    },
  },
});
