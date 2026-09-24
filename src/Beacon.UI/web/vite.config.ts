import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import fs from 'node:fs';
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

// MSW's service worker (public/mockServiceWorker.js) is dev-only tooling for
// `npm run dev:mock` — Vite copies public/ into the build output, so strip it
// after the bundle is written. It must never ship in src/Beacon.UI/wwwroot.
function stripMockServiceWorker(): Plugin {
  let outDir = 'dist';
  return {
    name: 'beacon:strip-mock-service-worker',
    apply: 'build',
    configResolved(config) {
      outDir = path.resolve(config.root, config.build.outDir);
    },
    closeBundle() {
      fs.rmSync(path.join(outDir, 'mockServiceWorker.js'), { force: true });
    },
  };
}

export default defineConfig({
  // Emitted asset URLs are absolute under /beacon, matching StaticWebAssetBasePath in the csproj
  // and BrowserRouter's basename. Absolute (not './') so a nested client route such as
  // /beacon/queries/5 still resolves /beacon/assets/*, rather than /beacon/queries/assets/*.
  base: '/beacon/',
  plugins: [react(), stripMockServiceWorker()],
  define: {
    'import.meta.env.BEACON_COMMIT_SHA': JSON.stringify(commitSha),
    'import.meta.env.BEACON_BUILD_DATE': JSON.stringify(buildDate),
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  // Output lands in src/Beacon.UI/wwwroot/beacon — the folder name IS the mount path. The
  // bundle is packed as plain content and copied into the consuming app's wwwroot as-is, so
  // nesting it here is what puts index.html and assets/ under /beacon for every consumer.
  // Must stay in step with ReactBuildOutputDir in the csproj and `base` above.
  build: {
    outDir: '../wwwroot/beacon',
    emptyOutDir: true,
    // 'hidden' emits .map files for tooling but omits the sourceMappingURL
    // reference from the served JS, so maps aren't exposed to production users.
    sourcemap: 'hidden',
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
