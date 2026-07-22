import { useCallback, useEffect, useRef, useState } from "react";
import { isRetriableRefreshError } from "./api";

export type RefreshState = {
  key: string;
  loading: boolean;
  refreshing: boolean;
  error: string;
  lastUpdatedAt: number | null;
  failureStartedAt: number | null;
  nextRetryAt: number | null;
  retryAttempt: number;
};

type PersistentRefreshOptions<T> = {
  key: string;
  enabled: boolean;
  load: () => Promise<T>;
  onSuccess?: (value: T) => void;
};

const retryDelaysMs = [1_000, 2_000, 5_000, 10_000, 30_000, 60_000];

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : "Unable to refresh data.";
}

function retryDelay(attempt: number) {
  const base = retryDelaysMs[Math.min(attempt, retryDelaysMs.length - 1)];
  const variation = base * 0.15;
  return Math.max(500, Math.round(base - variation + Math.random() * variation * 2));
}

export function usePersistentRefresh<T>({ key, enabled, load, onSuccess }: PersistentRefreshOptions<T>) {
  const [data, setDataState] = useState<T | null>(null);
  const dataRef = useRef<T | null>(null);
  const [state, setState] = useState<RefreshState>({
    key,
    loading: enabled,
    refreshing: false,
    error: "",
    lastUpdatedAt: null,
    failureStartedAt: null,
    nextRetryAt: null,
    retryAttempt: 0
  });
  const keyRef = useRef(key);
  const enabledRef = useRef(enabled);
  const loadRef = useRef(load);
  const onSuccessRef = useRef(onSuccess);
  const inFlightRef = useRef<{ key: string; promise: Promise<T | null> } | null>(null);
  const retryTimerRef = useRef(0);
  const retryAttemptRef = useRef(0);

  keyRef.current = key;
  enabledRef.current = enabled;
  loadRef.current = load;
  onSuccessRef.current = onSuccess;

  const clearRetry = useCallback(() => {
    window.clearTimeout(retryTimerRef.current);
    retryTimerRef.current = 0;
  }, []);

  const run = useCallback((targetKey: string): Promise<T | null> => {
    if (!enabledRef.current || targetKey !== keyRef.current)
      return Promise.resolve(null);
    if (inFlightRef.current?.key === targetKey)
      return inFlightRef.current.promise;

    clearRetry();
    setState(current => ({
      ...current,
      key: targetKey,
      loading: dataRef.current === null,
      refreshing: dataRef.current !== null,
      nextRetryAt: null
    }));

    const promise = loadRef.current()
      .then(value => {
        if (targetKey !== keyRef.current || !enabledRef.current)
          return null;
        const now = Date.now();
        retryAttemptRef.current = 0;
        dataRef.current = value;
        setDataState(value);
        setState({
          key: targetKey,
          loading: false,
          refreshing: false,
          error: "",
          lastUpdatedAt: now,
          failureStartedAt: null,
          nextRetryAt: null,
          retryAttempt: 0
        });
        onSuccessRef.current?.(value);
        return value;
      })
      .catch(error => {
        if (targetKey !== keyRef.current || !enabledRef.current)
          return null;
        const now = Date.now();
        const retryable = isRetriableRefreshError(error);
        const attempt = retryAttemptRef.current;
        const delay = retryable ? retryDelay(attempt) : 0;
        if (retryable)
          retryAttemptRef.current = attempt + 1;
        setState(current => ({
          ...current,
          key: targetKey,
          loading: false,
          refreshing: false,
          error: errorMessage(error),
          failureStartedAt: current.failureStartedAt ?? now,
          nextRetryAt: retryable ? now + delay : null,
          retryAttempt: retryable ? attempt + 1 : attempt
        }));
        if (retryable) {
          retryTimerRef.current = window.setTimeout(() => {
            retryTimerRef.current = 0;
            void run(targetKey);
          }, delay);
        }
        return null;
      })
      .finally(() => {
        if (inFlightRef.current?.promise === promise)
          inFlightRef.current = null;
      });
    inFlightRef.current = { key: targetKey, promise };
    return promise;
  }, [clearRetry]);

  const refresh = useCallback(() => run(keyRef.current), [run]);
  const refreshFresh = useCallback(async () => {
    const inFlight = inFlightRef.current;
    if (inFlight?.key === keyRef.current)
      await inFlight.promise;
    return run(keyRef.current);
  }, [run]);
  const setData = useCallback((value: T) => {
    const now = Date.now();
    dataRef.current = value;
    retryAttemptRef.current = 0;
    clearRetry();
    setDataState(value);
    setState({
      key: keyRef.current,
      loading: false,
      refreshing: false,
      error: "",
      lastUpdatedAt: now,
      failureStartedAt: null,
      nextRetryAt: null,
      retryAttempt: 0
    });
  }, [clearRetry]);

  useEffect(() => {
    clearRetry();
    retryAttemptRef.current = 0;
    if (!enabled) {
      setState(current => ({ ...current, key, loading: false, refreshing: false, nextRetryAt: null }));
      return;
    }
    setState(current => ({
      ...current,
      key,
      loading: dataRef.current === null,
      refreshing: dataRef.current !== null,
      error: "",
      failureStartedAt: null,
      nextRetryAt: null,
      retryAttempt: 0
    }));
    void run(key);
    return clearRetry;
  }, [clearRetry, enabled, key, run]);

  useEffect(() => () => clearRetry(), [clearRetry]);

  return { data, state, refresh, refreshFresh, setData };
}
