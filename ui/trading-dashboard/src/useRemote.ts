import { useEffect, useState } from 'react';
import { errorMessage } from './api';
import type { Remote } from './model';

export function useRemote<T>(load: (signal: AbortSignal) => Promise<T>, revision = 0): Remote<T> {
  const [result, setResult] = useState<{ load: typeof load; revision: number; value: Remote<T> } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setResult({ load, revision, value: { status: 'loading' } });
    void load(controller.signal).then(data => {
      if (!controller.signal.aborted) setResult({ load, revision, value: { status: 'ready', data } });
    }, error => {
      if (!controller.signal.aborted) setResult({ load, revision, value: { status: 'error', message: errorMessage(error) } });
    });
    return () => controller.abort();
  }, [load, revision]);
  return result?.load === load && result.revision === revision ? result.value : { status: 'loading' };
}
