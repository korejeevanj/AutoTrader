import { useEffect, useState } from 'react';
import { errorMessage, readHealth } from './api';
import type { ApiConnection } from './model';

export function Health() {
  const [api, setApi] = useState<ApiConnection>({ status: 'checking' });
  const [probe, setProbe] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setApi({ status: 'checking' });
    void readHealth(controller.signal).then(health => {
      if (!controller.signal.aborted) setApi({ status: 'connected', health });
    }, error => {
      if (!controller.signal.aborted) setApi({ status: 'unavailable', message: errorMessage(error) });
    });
    const timer = window.setTimeout(() => setProbe(p => p + 1), 15_000);
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [probe]);
  return <section aria-labelledby="api-heading" className="health-panel">
    <div><h2 id="api-heading">API connectivity</h2><p role="status" className={api.status === 'connected' ? 'positive' : api.status === 'unavailable' ? 'negative' : 'muted'}>{api.status === 'connected' ? 'Connected' : api.status === 'checking' ? 'Checking…' : 'Unavailable'}</p></div>
    <p className="helper">{api.status === 'connected' ? 'Health contract verified · API only, not feed freshness.' : api.status === 'checking' ? 'Verifying synthetic / paused health contract…' : api.message}</p>
    <button disabled={api.status === 'checking'} onClick={() => { setApi({ status: 'checking' }); setProbe(p => p + 1); }}>{api.status === 'unavailable' ? 'Retry connection' : 'Refresh connection'}</button>
  </section>;
}
