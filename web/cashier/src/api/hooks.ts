import { useCallback, useMemo, useState } from 'react';
import { useToast } from '../components/ui/Toast';
import { ApiError, newRequestKey } from './client';

/**
 * Обёртка над вызовами API: ошибку показывает тостом и возвращает null.
 * Страница получает либо результат, либо null — и во втором случае ничего
 * дополнительно сообщать не должна.
 */
export function useCall() {
  const toast = useToast();

  return useCallback(
    async <T,>(promise: Promise<T>): Promise<T | null> => {
      try {
        return await promise;
      } catch (e) {
        toast('error', e instanceof ApiError ? e.message : 'Неожиданная ошибка. Попробуйте ещё раз.');
        return null;
      }
    },
    [toast],
  );
}

/**
 * Ключ запроса для операций с деньгами. Один и тот же, пока не изменилось
 * содержимое формы: если связь оборвалась и администратор нажал кнопку ещё
 * раз, сервер узнает повтор и не проведёт операцию дважды. Изменили форму —
 * ключ новый. После успеха вызывается renew(), чтобы такая же операция
 * следом (вторая покупка на ту же сумму) считалась отдельной.
 */
export function useRequestKey(content: unknown): [string, () => void] {
  const [generation, setGeneration] = useState(0);
  const fingerprint = JSON.stringify(content);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  const key = useMemo(() => newRequestKey(), [fingerprint, generation]);
  const renew = useCallback(() => setGeneration((g) => g + 1), []);

  return [key, renew];
}
