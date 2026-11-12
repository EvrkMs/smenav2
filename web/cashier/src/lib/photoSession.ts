import { useCallback, useEffect, useState } from 'react';
import { cancelPhotoSession, createPhotoSession, getPhotoSession, type PhotoSession } from '../api/client';
import { usePersistedState } from './hooks';

/** Как часто касса спрашивает сервер, сколько фото уже загружено с телефона. */
const POLL_INTERVAL_MS = 2000;

/**
 * Сессия фото текущей смены. Её номер переживает перезапуск программы — как и
 * остальной ввод отчёта: иначе после перезапуска пришлось бы создавать новую
 * сессию, а сервер при этом стирает уже загруженные фото.
 */
export function usePhotoSession() {
  const [sessionId, setSessionId] = usePersistedState<string | null>('raport_photoSession', null);
  const [loaded, setLoaded] = useState<PhotoSession | null>(null);

  useEffect(() => {
    if (!sessionId) return;

    let stopped = false;
    const load = async () => {
      try {
        const session = await getPhotoSession(sessionId);
        if (stopped) return;
        // Сессии больше нет (смена с ней закрыта или создана новая) — забываем её.
        if (session === null) setSessionId(null);
        else setLoaded(session);
      } catch {
        // Нет связи: показываем последнее известное, следующий опрос повторит.
      }
    };

    void load();
    const timer = window.setInterval(() => void load(), POLL_INTERVAL_MS);
    return () => {
      stopped = true;
      window.clearInterval(timer);
    };
  }, [sessionId, setSessionId]);

  // Ответ от прежней сессии не показываем под видом текущей.
  const session = sessionId !== null && loaded?.id === sessionId ? loaded : null;

  const start = useCallback(async () => {
    const created = await createPhotoSession();
    setLoaded(created);
    setSessionId(created.id);
  }, [setSessionId]);

  const cancel = useCallback(async () => {
    if (sessionId) await cancelPhotoSession(sessionId);
    setSessionId(null);
  }, [sessionId, setSessionId]);

  /** Смена закрыта: сервер сам закрыл сессию, осталось её забыть. */
  const forget = useCallback(() => setSessionId(null), [setSessionId]);

  return { sessionId, session, start, cancel, forget };
}
