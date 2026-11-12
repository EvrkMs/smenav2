const money = new Intl.NumberFormat('ru-RU');

/** 12 000 ₽; минус — обычным знаком перед числом. */
export const formatMoney = (value: number) => `${money.format(value)} ₽`;

/** Со знаком: +1 600 ₽ / −500 ₽. */
export const formatSigned = (value: number) => (value > 0 ? `+${formatMoney(value)}` : formatMoney(value));

const dayFormat = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
const timeFormat = new Intl.DateTimeFormat('ru-RU', { hour: '2-digit', minute: '2-digit' });

export const formatDay = (iso: string) => dayFormat.format(new Date(iso));

export const formatTime = (iso: string) => timeFormat.format(new Date(iso));
