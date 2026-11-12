import { useCallback, useEffect, useMemo, useState } from 'react';
import { useToast } from '../components/ui/Toast';
import { ApiError, newRequestKey } from './client';

/** Обёртка над вызовом API: ошибку показывает тостом и возвращает null. */
export function useCall() {
  const toast = useToast();

  return useCallback(
    async <T,>(promise: Promise<T>): Promise<T | null> => {
      try {
        return await promise;
      } catch (e) {
        // Об истёкшей сессии сообщает экран входа, тост не нужен.
        if (!(e instanceof ApiError && e.kind === 'unauthorized')) {
          toast('error', e instanceof ApiError ? e.message : 'Неожиданная ошибка. Попробуйте ещё раз.');
        }
        return null;
      }
    },
    [toast],
  );
}

/**
 * Загрузка данных для страницы: значение, признак загрузки и функция перезагрузки.
 * Ошибка показывается тостом, прежние данные остаются на экране.
 */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): { data: T | null; loading: boolean; reload: () => void } {
  const call = useCall();
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    void call(load()).then((result) => {
      if (cancelled) return;
      if (result !== null) setData(result);
      setLoading(false);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);
  return { data, loading, reload };
}

/**
 * Ключ запроса для денежной операции: один и тот же, пока не изменилась форма
 * (повтор после обрыва связи не проведёт операцию дважды); после успеха — renew().
 */
export function useRequestKey(content: unknown): [string, () => void] {
  const [generation, setGeneration] = useState(0);
  const fingerprint = JSON.stringify(content);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const key = useMemo(() => newRequestKey(), [fingerprint, generation]);
  const renew = useCallback(() => setGeneration((g) => g + 1), []);

  return [key, renew];
}
