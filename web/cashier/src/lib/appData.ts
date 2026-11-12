import { useSyncExternalStore } from 'react';
import { getConfig, getEmployees, getSafe, type Employee, type ShiftConfig } from '../api/client';

// ---------------------------------------------------------------------------
// Общие данные приложения: сотрудники, сейф, настройки смены.
//
// Вкладки живут всё время работы приложения (App.tsx их не размонтирует),
// поэтому данные, загруженные страницей «на mount», устаревали бы. Здесь один
// источник правды: refresh* обновляет все вкладки разом.
//
// Ошибки здесь НЕ тостятся и не глотаются — refresh* бросает, а вызывающий
// (App) решает, что показать: разовый тост или бейдж «нет связи».
// ---------------------------------------------------------------------------

type Listener = () => void;

const fallbackConfig: ShiftConfig = { cashFloat: 1000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3 };

let employees: Employee[] = [];
let safe: number | null = null;
let config: ShiftConfig = fallbackConfig;

const listeners = new Set<Listener>();

function emit() {
  listeners.forEach((l) => l());
}

function subscribe(listener: Listener) {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

export function useEmployees(): Employee[] {
  return useSyncExternalStore(subscribe, () => employees);
}

export function useSafe(): number | null {
  return useSyncExternalStore(subscribe, () => safe);
}

export function useConfig(): ShiftConfig {
  return useSyncExternalStore(subscribe, () => config);
}

/** Баланс сейфа из ответа сервера на операцию — без лишнего запроса. */
export function setSafe(value: number): void {
  safe = value;
  emit();
}

export async function refreshEmployees(): Promise<void> {
  employees = await getEmployees();
  emit();
}

export async function refreshSafe(): Promise<void> {
  safe = await getSafe();
  emit();
}

async function refreshConfig(): Promise<void> {
  config = await getConfig();
  emit();
}

/** Обновляет всё разом; бросает первую ошибку, если хотя бы один запрос упал. */
export async function refreshAll(): Promise<void> {
  const results = await Promise.allSettled([refreshEmployees(), refreshSafe(), refreshConfig()]);
  const failed = results.find((r): r is PromiseRejectedResult => r.status === 'rejected');
  if (failed) throw failed.reason;
}
