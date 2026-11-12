// ---------------------------------------------------------------------------
// Клиент REST API кассы. Интерфейс раздаётся тем же сервером, поэтому адреса
// относительные; токен устройства приходит от оболочки (window.__SMENA__)
// или, при работе в обычном браузере, из localStorage.
//
// Ничего не считает сам: все формулы и правила — на сервере.
// ---------------------------------------------------------------------------

declare global {
  interface Window {
    /** Заполняет оболочка WebView2 до загрузки страницы. */
    __SMENA__?: { token?: string };
  }
}

const TOKEN_STORAGE_KEY = 'smena.deviceToken';

export function getDeviceToken(): string | null {
  return window.__SMENA__?.token || localStorage.getItem(TOKEN_STORAGE_KEY);
}

/** Только для работы в браузере без оболочки: оболочка передаёт токен сама. */
export function saveDeviceToken(token: string): void {
  localStorage.setItem(TOKEN_STORAGE_KEY, token.trim());
}

export function forgetDeviceToken(): void {
  localStorage.removeItem(TOKEN_STORAGE_KEY);
}

export type ApiErrorKind = 'network' | 'unauthorized' | 'rejected' | 'server';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;

  constructor(kind: ApiErrorKind, message: string) {
    super(message);
    this.kind = kind;
  }
}

/**
 * UUID для заголовка Idempotency-Key. crypto.randomUUID() доступен только в
 * защищённом контексте (https или localhost), а касса открывает сервер по
 * http в локальной сети — поэтому собираем UUID v4 из getRandomValues.
 */
export function newRequestKey(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

interface RequestOptions {
  body?: unknown;
  requestKey?: string;
  /** Статусы, тело которых вызывающий разбирает сам, а не получает исключением. */
  passThrough?: number[];
}

async function request(method: 'GET' | 'POST' | 'DELETE', path: string, options: RequestOptions = {}): Promise<Response> {
  const headers: Record<string, string> = {};
  const token = getDeviceToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  if (options.requestKey) headers['Idempotency-Key'] = options.requestKey;

  let response: Response;
  try {
    response = await fetch(`/api${path}`, {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
    });
  } catch {
    throw new ApiError('network', 'Нет связи с сервером.');
  }

  if (response.ok || options.passThrough?.includes(response.status)) return response;

  if (response.status === 401) {
    throw new ApiError('unauthorized', 'Сервер не принял токен устройства.');
  }

  // 400 и 422 несут { message } с текстом для администратора.
  const message = await response
    .json()
    .then((data: { message?: string }) => data.message)
    .catch(() => undefined);

  throw new ApiError(
    response.status === 422 || response.status === 400 ? 'rejected' : 'server',
    message ?? `Ошибка сервера (${response.status}).`,
  );
}

async function get<T>(path: string): Promise<T> {
  return (await request('GET', path)).json() as Promise<T>;
}

async function post<T>(path: string, body: unknown, requestKey?: string): Promise<T> {
  return (await request('POST', path, { body, requestKey })).json() as Promise<T>;
}

// ---- Типы ответа сервера (см. Smena.Server/Cashier/CashierContracts.cs) ----

export interface ShiftConfig {
  cashFloat: number;
  maxEmployees: number;
  maxTotalHours: number;
  minPhotos: number;
}

export interface Employee {
  id: string;
  name: string;
  hourlyRate: number;
  /** Сколько клуб должен сотруднику; отрицательное — сотрудник в минусе. */
  balance: number;
}

export interface Category {
  id: number;
  name: string;
  commentRequired: boolean;
}

export interface ShiftInput {
  factCash: number;
  factNonCash: number;
  skladCash: number;
  skladNonCash: number;
  langameCash: number;
  langameNonCash: number;
  factSafe: number;
  shortageReason: string | null;
  employees: { employeeId: string; hours: number; minus: number }[];
  /** Сессия, в которую сняты фото к этой смене. */
  photoSessionId: string | null;
}

export interface Photo {
  id: string;
  takenAt: string | null;
  /** Старый снимок или без времени съёмки: принят, но будет отмечен в отчёте. */
  isStale: boolean;
  uploadedAt: string;
  /** Снимок экрана, вставленный на кассе из буфера обмена. */
  pastedAtCashier: boolean;
}

export interface PhotoSession {
  id: string;
  /** Ссылка для QR: страница загрузки, открывается с телефона. */
  uploadUrl: string;
  minPhotos: number;
  maxPhotos: number;
  photos: Photo[];
}

export interface ShiftProblem {
  code: string;
  message: string;
  employeeId: string | null;
}

export interface ShiftCalculation {
  cashFloat: number;
  expectedSafe: number;
  programTotal: number;
  revenue: number;
  cashToSafe: number;
  nonCash: number;
  cashDiscrepancy: number;
  safeDiscrepancy: number;
  requiredMinus: number;
  enteredMinus: number;
  payrollAccrued: number;
  payrollNet: number;
  total: number;
  safeAfter: number;
  canClose: boolean;
  employees: {
    employeeId: string;
    name: string;
    hours: number;
    hourlyRate: number;
    accrued: number;
    minus: number;
    payable: number;
  }[];
  problems: ShiftProblem[];
}

export type ShiftCloseResult =
  | { closed: true; shiftId: string; replayed: boolean }
  | { closed: false; calculation: ShiftCalculation };

export interface CashOperationResult {
  operationId: string;
  replayed: boolean;
  safeBalance: number;
}

// ---- Вызовы ----

export const getConfig = () => get<ShiftConfig>('/config');

export const getEmployees = () => get<Employee[]>('/employees');

export const getSafe = () => get<{ balance: number }>('/safe').then((r) => r.balance);

export const getCategories = (kind: 'safeIncome' | 'safeExpense') => get<Category[]>(`/categories?kind=${kind}`);

export const calculateShift = (input: ShiftInput) => post<ShiftCalculation>('/shifts/calc', input);

/** 422 здесь — не ошибка связи, а отказ с расчётом и списком проблем. */
export async function closeShift(input: ShiftInput, requestKey: string): Promise<ShiftCloseResult> {
  const response = await request('POST', '/shifts', { body: input, requestKey, passThrough: [422] });

  if (response.status === 422) {
    const rejected = (await response.json()) as { calculation: ShiftCalculation };
    return { closed: false, calculation: rejected.calculation };
  }

  const closed = (await response.json()) as { shiftId: string; replayed: boolean };
  return { closed: true, shiftId: closed.shiftId, replayed: closed.replayed };
}

/** Новая сессия фото. Прежние неотправленные фото сервер при этом стирает. */
export const createPhotoSession = () => post<PhotoSession>('/photo-sessions', undefined);

/** null — сессии больше нет: смена с ней уже закрыта или создана новая. */
export async function getPhotoSession(id: string): Promise<PhotoSession | null> {
  const response = await request('GET', `/photo-sessions/${id}`, { passThrough: [404] });
  return response.status === 404 ? null : ((await response.json()) as PhotoSession);
}

/** «Сбросить фото»: сессия и загруженные в неё снимки удаляются. */
export async function cancelPhotoSession(id: string): Promise<void> {
  await request('DELETE', `/photo-sessions/${id}`);
}

/**
 * Снимок экрана с кассы в сессию фото. Тело запроса — сам файл, как и при
 * загрузке с телефона. Отказ (не картинка, повтор, сессия закрыта) приходит
 * исключением с текстом для администратора.
 */
export async function pastePhoto(sessionId: string, image: Blob): Promise<Photo> {
  const headers: Record<string, string> = {};
  const token = getDeviceToken();
  if (token) headers.Authorization = `Bearer ${token}`;

  let response: Response;
  try {
    response = await fetch(`/api/photo-sessions/${sessionId}/photos`, { method: 'POST', headers, body: image });
  } catch {
    throw new ApiError('network', 'Нет связи с сервером.');
  }

  if (response.ok) return (await response.json()) as Photo;
  if (response.status === 401) throw new ApiError('unauthorized', 'Сервер не принял токен устройства.');

  const message = await response
    .json()
    .then((data: { message?: string }) => data.message)
    .catch(() => undefined);
  throw new ApiError(response.status >= 500 ? 'server' : 'rejected', message ?? `Ошибка сервера (${response.status}).`);
}

/** Убирает снимок из ещё не отправленной сессии. */
export async function removePhoto(sessionId: string, photoId: string): Promise<void> {
  await request('DELETE', `/photo-sessions/${sessionId}/photos/${photoId}`);
}

export interface StockItem {
  /** Устойчивый ключ позиции: по нему товар узнаётся после обновления остатков. */
  id: string;
  name: string;
  article: string | null;
  folder: string | null;
  /** Остаток по «МойСклад». Бывает дробным и отрицательным. */
  stock: number;
}

export interface Stock {
  /** false — доступ к «МойСклад» на сервере не настроен. */
  enabled: boolean;
  /** Когда сервер в последний раз удачно загрузил остатки; null — ещё ни разу. */
  refreshedAt: string | null;
  /** Почему не удалась последняя попытка обновления; остатки при этом прежние. */
  error: string | null;
  items: StockItem[];
}

/** Остатки «МойСклад» из памяти сервера — мгновенно. */
export const getStock = () => get<Stock>('/stock');

/** Просит сервер перечитать остатки из «МойСклад» (не чаще раза в полминуты). */
export const refreshStock = () => post<Stock>('/stock/refresh', undefined);

export const payout = (
  body: { employeeId: string; amount: number; isSalary: boolean; comment: string | null },
  requestKey: string,
) => post<CashOperationResult>('/payouts', body, requestKey);

export const safeIncome = (body: { categoryId: number; amount: number; comment: string | null }, requestKey: string) =>
  post<CashOperationResult>('/safe/income', body, requestKey);

export const safeExpense = (body: { categoryId: number; amount: number; comment: string | null }, requestKey: string) =>
  post<CashOperationResult>('/safe/expense', body, requestKey);
