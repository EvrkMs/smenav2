// ---------------------------------------------------------------------------
// Клиент API панели (/api/root). Вход — по cookie, которую ставит сервер;
// браузер прикладывает её сам. Все правила и расчёты — на сервере.
// ---------------------------------------------------------------------------

export type ApiErrorKind = 'network' | 'unauthorized' | 'rejected' | 'server';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;

  constructor(kind: ApiErrorKind, message: string) {
    super(message);
    this.kind = kind;
  }
}

/** UUID для Idempotency-Key. crypto.randomUUID() недоступен по http вне localhost. */
export function newRequestKey(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Вызывается, когда сервер отвечает 401 на любой запрос, кроме входа: сессия истекла. */
let onUnauthorized: () => void = () => undefined;
export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler;
}

async function request<T>(method: 'GET' | 'POST' | 'PUT', path: string, body?: unknown, requestKey?: string): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (requestKey) headers['Idempotency-Key'] = requestKey;

  let response: Response;
  try {
    response = await fetch(`/api/root${path}`, {
      method,
      headers,
      credentials: 'same-origin',
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError('network', 'Нет связи с сервером.');
  }

  if (response.ok) {
    return (response.status === 204 ? undefined : await response.json()) as T;
  }

  const message = await response
    .json()
    .then((data: { message?: string }) => data.message)
    .catch(() => undefined);

  if (response.status === 401 && path !== '/login') {
    onUnauthorized();
    throw new ApiError('unauthorized', 'Сессия истекла. Войдите снова.');
  }

  const kind: ApiErrorKind = [400, 401, 422, 429].includes(response.status) ? 'rejected' : 'server';
  throw new ApiError(kind, message ?? `Ошибка сервера (${response.status}).`);
}

const get = <T,>(path: string) => request<T>('GET', path);
const post = <T,>(path: string, body?: unknown, key?: string) => request<T>('POST', path, body, key);
const put = <T,>(path: string, body: unknown) => request<T>('PUT', path, body);

// ---- Типы (см. Smena.Server/Root/RootContracts.cs) ----

export interface Overview {
  safe: number;
  bankConfirmed: number;
  bankInTransit: number;
  owedToEmployees: number;
  employeeDebts: number;
  pendingReceipts: number;
  outboxPending: number;
  outboxFailed: number;
}

export interface Employee {
  id: string;
  name: string;
  telegramId: number;
  telegramUsername: string | null;
  hourlyRate: number;
  isActive: boolean;
  salaryThreadId: number | null;
  balance: number;
}

export interface OperationListItem {
  id: string;
  type: string;
  typeName: string;
  occurredAt: string;
  comment: string;
  actorName: string;
  isCancelled: boolean;
  lines: { amount: number; note: string | null }[];
}

export interface OperationDetail {
  id: string;
  type: string;
  typeName: string;
  occurredAt: string;
  comment: string;
  actorName: string;
  cancelledAt: string | null;
  cancelledBy: string | null;
  cancelReason: string | null;
  transactions: {
    amount: number;
    note: string | null;
    entries: { account: string; direction: number; category: string | null }[];
  }[];
  shift: {
    factCash: number;
    factNonCash: number;
    skladCash: number;
    skladNonCash: number;
    langameCash: number;
    langameNonCash: number;
    factSafe: number;
    expectedSafe: number;
    cashFloat: number;
    cashDiscrepancy: number;
    safeDiscrepancy: number;
    shortageReason: string | null;
    employees: { name: string; hours: number; hourlyRate: number; minus: number }[];
    /** isStale — старый снимок или без времени съёмки. Файл: /api/root/photos/{id}. */
    photos: { id: string; takenAt: string | null; isStale: boolean; uploadedAt: string; pastedAtCashier: boolean }[];
  } | null;
}

export interface Receipt {
  id: string;
  operationId: string;
  channel: string;
  note: string | null;
  amount: number;
  sentAt: string;
  expectedAt: string | null;
  expectedAmount: number | null;
  confirmedAt: string | null;
  confirmedAmount: number | null;
  isDue: boolean;
}

export type CategoryKind = 'SafeIncome' | 'SafeExpense' | 'BankIncome' | 'BankExpense' | 'Revenue' | 'Expense' | 'Employee';

export interface Category {
  id: number;
  kind: CategoryKind;
  name: string;
  isSystem: boolean;
  isArchived: boolean;
  counterAccount: string | null;
  counterCategory: string | null;
}

export interface Device {
  id: string;
  name: string;
  createdAt: string;
  revokedAt: string | null;
}

export interface Channel {
  id: number;
  name: string;
  delayDays: number | null;
  feePercent: number | null;
}

export interface Settings {
  cashFloat: number;
  maxEmployeesPerShift: number;
  maxHoursPerShift: number;
  minShiftPhotos: number;
  photoMaxAgeHours: number;
  timeZoneId: string;
  telegramChatId: number | null;
  telegramReportsThreadId: number | null;
  telegramSafeThreadId: number | null;
  telegramExpensesThreadId: number | null;
  telegramSalaryChatId: number | null;
  channels: Channel[];
}

// ---- Вызовы ----

export const api = {
  login: (username: string, password: string) => post<{ username: string }>('/login', { username, password }),
  logout: () => post<void>('/logout'),
  me: () => get<{ username: string }>('/me'),
  changePassword: (currentPassword: string, newPassword: string) => post<void>('/password', { currentPassword, newPassword }),

  overview: () => get<Overview>('/overview'),

  employees: () => get<Employee[]>('/employees'),
  createEmployee: (body: { telegramId: number; name: string; hourlyRate: number; salaryThreadId: number | null }) =>
    post<Employee>('/employees', body),
  updateEmployee: (id: string, body: { name: string; hourlyRate: number; isActive: boolean; salaryThreadId: number | null }) =>
    put<void>(`/employees/${id}`, body),

  operations: (filter: { type?: string; cancelled?: boolean }) => {
    const query = new URLSearchParams();
    if (filter.type) query.set('type', filter.type);
    if (filter.cancelled !== undefined) query.set('cancelled', String(filter.cancelled));
    return get<OperationListItem[]>(`/operations?${query}`);
  },
  operation: (id: string) => get<OperationDetail>(`/operations/${id}`),
  cancelOperation: (id: string, reason: string) => post<void>(`/operations/${id}/cancel`, { reason }),

  payout: (
    body: { employeeId: string; amount: number; fromBank: boolean; isSalary: boolean; allowOverdraft: boolean; comment: string | null },
    key: string,
  ) => post<{ operationId: string }>('/payouts', body, key),
  fine: (body: { employeeId: string; amount: number; comment: string | null }, key: string) =>
    post<{ operationId: string }>('/fines', body, key),
  bonus: (body: { employeeId: string; amount: number; comment: string | null }, key: string) =>
    post<{ operationId: string }>('/bonuses', body, key),
  inventory: (body: { employeeIds: string[]; totalAmount: number; comment: string | null }, key: string) =>
    post<{ operationId: string }>('/inventory', body, key),
  opening: (
    body: { target: 'Safe' | 'Bank' | 'Employee'; employeeId: string | null; amount: number; comment: string | null },
    key: string,
  ) => post<{ operationId: string }>('/opening', body, key),
  bankMove: (direction: 'income' | 'expense', body: { categoryId: number; amount: number; comment: string | null }, key: string) =>
    post<{ operationId: string }>(`/bank/${direction}`, body, key),

  receipts: (pending: boolean) => get<Receipt[]>(`/bank/receipts${pending ? '?pending=true' : ''}`),
  confirmReceipt: (id: string, amount: number) => post<{ fee: number }>(`/bank/receipts/${id}/confirm`, { amount }),
  unconfirmReceipt: (id: string) => post<void>(`/bank/receipts/${id}/unconfirm`),

  categories: () => get<Category[]>('/categories'),
  createCategory: (body: { kind: CategoryKind; name: string; counter: string | null }) => post<Category>('/categories', body),
  updateCategory: (id: number, body: { name: string; isArchived: boolean }) => put<void>(`/categories/${id}`, body),

  devices: () => get<Device[]>('/devices'),
  createDevice: (name: string) => post<{ id: string; name: string; token: string }>('/devices', { name }),
  revokeDevice: (id: string) => post<void>(`/devices/${id}/revoke`),

  settings: () => get<Settings>('/settings'),
  saveSettings: (body: Settings) => put<void>('/settings', body),
};
