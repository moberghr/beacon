var _a;
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'node:path';
var backend = (_a = process.env.BEACON_BACKEND_URL) !== null && _a !== void 0 ? _a : 'https://localhost:7187';
export default defineConfig({
    base: '/',
    plugins: [react()],
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
