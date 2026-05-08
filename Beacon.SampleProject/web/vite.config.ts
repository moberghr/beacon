import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';

const backend = process.env.BEACON_BACKEND_URL ?? 'https://localhost:7187';

export default defineConfig({
  base: '/',
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: 'dist',
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
