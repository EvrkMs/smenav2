export function formatMoney(value: number): string {
  return new Intl.NumberFormat('ru-RU').format(Math.round(value));
}

export function formatSigned(value: number): string {
  return value > 0 ? `+${formatMoney(value)}` : formatMoney(value);
}

export function parseIntSafe(value: string, fallback = 0): number {
  const n = parseInt(value, 10);
  return Number.isFinite(n) ? n : fallback;
}

/** Целое со знаком из поля ввода: «-1500» → −1500. */
export function parseSignedInt(value: string): number {
  const n = parseInt(value.replace(/[^\d-]/g, ''), 10);
  return Number.isFinite(n) ? n : 0;
}

/** Пустая строка → null, иначе число. Для необязательных числовых настроек. */
export function parseOptionalNumber(value: string): number | null {
  const trimmed = value.trim().replace(',', '.');
  if (trimmed === '') return null;
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : null;
}

const dateTime = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
const dateOnly = new Intl.DateTimeFormat('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });

/** Сервер отдаёт время в UTC; показываем в часовом поясе браузера. */
export function formatDateTime(iso: string | null): string {
  return iso ? dateTime.format(new Date(iso)) : '—';
}

export function formatDate(iso: string | null): string {
  return iso ? dateOnly.format(new Date(iso)) : '—';
}

export const signClass = (value: number) => (value < 0 ? 'value-negative' : value > 0 ? 'value-positive' : '');
