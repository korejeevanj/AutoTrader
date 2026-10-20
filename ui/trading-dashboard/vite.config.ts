import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
const apiPort = Number(process.env.AUTOTRADER_E2E_API_PORT ?? 5080);
if (!Number.isInteger(apiPort) || apiPort <= 1024 || apiPort >= 65536) throw new Error('Invalid API proxy port');

export default defineConfig({
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: `http://localhost:${apiPort}` },
      '/hubs': { target: `http://localhost:${apiPort}`, ws: true },
    },
  },
});
