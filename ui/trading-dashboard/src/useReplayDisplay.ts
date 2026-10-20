import { useEffect, useLayoutEffect, useState, useSyncExternalStore } from 'react';
import * as transport from './replayApi';
import { ReplayDisplay } from './replayDisplay';

export function useReplayDisplay(instrumentId: string | null) {
  const [display] = useState(() => new ReplayDisplay({
    transport, storage: window.localStorage, selectionId: () => crypto.randomUUID(),
    timers: { setTimeout: (callback, milliseconds) => window.setTimeout(callback, milliseconds), clearTimeout: id => window.clearTimeout(id) },
  }));
  const snapshot = useSyncExternalStore(display.subscribe, display.getSnapshot);
  useLayoutEffect(() => { display.setInspectionContext(instrumentId); }, [display, instrumentId]);
  useEffect(() => { display.start(); return () => display.stop(); }, [display]);
  return [snapshot, display] as const;
}
