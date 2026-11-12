// Клиент API кабинета сотрудника (см. Smena.Server/MiniApp/MiniAppEndpoints.cs).
// Кто сотрудник, сервер определяет по подписи Telegram в заголовке; никаких
// идентификаторов в запросах нет.

export interface Profile {
  name: string;
  hourlyRate: number;
  /** Сколько клуб должен сотруднику; отрицательное — сотрудник должен клубу. */
  balance: number;
}

export interface Movement {
  id: number;
  occurredAt: string;
  /** Плюс — начислено, минус — выплачено или удержано. */
  amount: number;
  title: string;
  comment: string | null;
}

export type ApiErrorKind = 'network' | 'unauthorized' | 'server';

export class ApiError extends Error {
  readonly kind: ApiErrorKind;

  constructor(kind: ApiErrorKind) {
    super(kind);
    this.kind = kind;
  }
}

export const PAGE_SIZE = 30;

async function get<T>(initData: string, path: string): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`/api/me${path}`, { headers: { Authorization: `tma ${initData}` }, cache: 'no-store' });
  } catch {
    throw new ApiError('network');
  }

  if (response.status === 401) throw new ApiError('unauthorized');
  if (!response.ok) throw new ApiError('server');
  return response.json() as Promise<T>;
}

export const getProfile = (initData: string) => get<Profile>(initData, '');

/** Страница истории, от новых к старым. before — id последней уже показанной строки. */
export const getMovements = (initData: string, before?: number) =>
  get<Movement[]>(initData, `/operations?take=${PAGE_SIZE}${before === undefined ? '' : `&before=${before}`}`);
