import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
const apiPort = Number(process.env.AUTOTRADER_E2E_API_PORT ?? 5080);
if (!Number.isInteger(apiPort) || apiPort <= 1024 || apiPort >= 65536) throw new Error('Invalid API proxy port');
// Optional two-process mode: the live instance (apiPort) keeps the live feed, status and commands; a research instance serves everything else,
// including recordings and replays, so research work can never slow the live path.
const researchPort = process.env.AUTOTRADER_RESEARCH_PORT ? Number(process.env.AUTOTRADER_RESEARCH_PORT) : undefined;
if (researchPort !== undefined && (!Number.isInteger(researchPort) || researchPort <= 1024 || researchPort >= 65536)) throw new Error('Invalid research proxy port');
const research = researchPort === undefined ? undefined : { target: `http://localhost:${researchPort}` };

export default defineConfig({
  plugins: [react()],
  server: {
    host: 'localhost',
    port: 5173,
    strictPort: true,
    proxy: {
      ...(research ? { '/api/live/replays': research, '/api/live/rehearsal': research, '/api/live/recordings': research } : {}),
      '/api/live': { target: `http://localhost:${apiPort}` },
      '/api': research ?? { target: `http://localhost:${apiPort}` },
      '/hubs': { target: `http://localhost:${apiPort}`, ws: true },
    },
  },
});
