// ---------------------------------------------------------------------------
// Связь с Telegram без его скрипта telegram-web-app.js.
//
// Скрипт грузится с telegram.org, который у сотрудника может не открываться.
// Всё, что нам от него нужно, Telegram и так передаёт в адресе страницы:
//   https://сервер/miniapp/#tgWebAppData=<initData>&tgWebAppThemeParams=<json>&…
// initData — подписанная строка, по которой сервер узнаёт сотрудника.
// ---------------------------------------------------------------------------

const STORAGE_KEY = 'smena.tgInitData';

declare global {
  interface Window {
    /** Мост к приложению Telegram на Android и iOS. */
    TelegramWebviewProxy?: { postEvent(eventType: string, eventData: string): void };
  }
}

export interface Launch {
  /** null — страница открыта не из Telegram. */
  initData: string | null;
  /** Цвета темы Telegram: bg_color, text_color, hint_color, button_color… */
  theme: Record<string, string>;
}

export function readLaunch(): Launch {
  const params = new URLSearchParams(window.location.hash.slice(1));

  // URLSearchParams уже раскодировал значение: это и есть строка initData как она подписана.
  let initData = params.get('tgWebAppData');

  // После перезагрузки страницы внутри Telegram фрагмент адреса может пропасть,
  // поэтому строку запоминаем на время жизни вкладки.
  try {
    if (initData) sessionStorage.setItem(STORAGE_KEY, initData);
    else initData = sessionStorage.getItem(STORAGE_KEY);
  } catch {
    // Хранилище недоступно — работаем с тем, что есть в адресе.
  }

  let theme: Record<string, string> = {};
  try {
    const raw = params.get('tgWebAppThemeParams');
    if (raw) theme = JSON.parse(raw) as Record<string, string>;
  } catch {
    // Тема не разобралась — останутся цвета по умолчанию.
  }

  return { initData: initData || null, theme };
}

/** Переносит цвета Telegram в переменные CSS, чтобы кабинет выглядел как остальной Telegram. */
export function applyTheme(theme: Record<string, string>): void {
  const style = document.documentElement.style;
  for (const [key, value] of Object.entries(theme)) {
    if (/^#[0-9a-f]{6}$/i.test(value)) style.setProperty(`--tg-${key.replaceAll('_', '-')}`, value);
  }
}

/**
 * Сообщает Telegram, что страница готова, и просит развернуть её на весь экран.
 * Необязательно (без этого Telegram сам уберёт заставку после загрузки страницы),
 * поэтому любые ошибки молча пропускаются.
 */
export function notifyReady(): void {
  for (const eventType of ['web_app_ready', 'web_app_expand']) {
    try {
      if (window.TelegramWebviewProxy) {
        window.TelegramWebviewProxy.postEvent(eventType, '');
      } else if (window.parent !== window) {
        // Веб-версия Telegram: страница открыта во фрейме.
        window.parent.postMessage(JSON.stringify({ eventType, eventData: '' }), 'https://web.telegram.org');
      }
    } catch {
      // Не Telegram или старая версия — ничего страшного.
    }
  }
}
