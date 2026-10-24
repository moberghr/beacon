import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
import fs from 'node:fs';

const backend = process.env.BEACON_BACKEND_URL ?? 'https://localhost:7187';

// No build identity (commit, build date) is written into the bundle: everything under /beacon/assets is served before
// sign-in. The signed-in shell reads the running build from GET /beacon/api/auth/permissions instead.

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
    // Source maps are not part of the shipped bundle: everything in wwwroot/beacon is packed and
    // served to anonymous callers, and the maps carry the full TSX sources. Set VITE_SOURCEMAP=1
    // for a local build that needs them ('hidden' omits the sourceMappingURL from the served JS).
    sourcemap: process.env.VITE_SOURCEMAP === '1' ? 'hidden' : false,
    // Two pages: the app, and the embed view (embed.html → src/embed/main.tsx), which the embed route serves under a
    // strict CSP. The embed entry imports none of the app (no sign-in, router or sidebar); shared modules such as React
    // land in common chunks.
    rollupOptions: {
      input: {
        main: path.resolve(__dirname, 'index.html'),
        embed: path.resolve(__dirname, 'embed.html'),
      },
    },
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
      // Only the MCP endpoint itself — a bare '/beacon/mcp' prefix would also catch the
      // /beacon/mcp-playground, /beacon/mcp-learning and /beacon/mcp-settings SPA routes.
      '^/beacon/mcp(/|$)': {
        target: backend,
        secure: false,
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
