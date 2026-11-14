import { QRCodeSVG } from 'qrcode.react';
import type { PhotoSession } from '../api/client';
import { Button, Panel } from './ui/primitives';
import './photoPanel.css';

interface Props {
  /** Минимум фото из настроек клуба. */
  minPhotos: number;
  /** Номер сессии известен, но сервер её ещё не отдал (только что открыли программу). */
  loading: boolean;
  session: PhotoSession | null;
  busy: boolean;
  /** Показать QR для съёмки с телефона. */
  onStart: () => void;
  /** Открыть окно вставки снимка экрана из буфера обмена. */
  onPaste: () => void;
  onCancel: () => void;
}

/**
 * Фото к смене. Два способа: вставить снимок экрана прямо на кассе или снять
 * с телефона по QR (чек терминала на кассе не вставишь). Здесь видно только,
 * сколько фото уже загружено.
 */
export default function PhotoPanel({ minPhotos, loading, session, busy, onStart, onPaste, onCancel }: Props) {
  if (!session) {
    return (
      <Panel title="Фото">
        <p className="muted photo-hint">
          {minPhotos > 0 ? `К смене нужно не меньше ${minPhotos} фото: ` : 'К смене можно приложить фото: '}
          экран LanGame, экран «МойСклад», чек сверки итогов с терминала.
        </p>
        <div className="photo-actions">
          <Button disabled={busy || loading} onClick={onPaste}>
            {loading ? 'Загружаю…' : 'Вставить фото'}
          </Button>
          <Button variant="ghost" disabled={busy || loading} onClick={onStart}>
            QR для телефона
          </Button>
        </div>
      </Panel>
    );
  }

  const count = session.photos.length;
  const stale = session.photos.filter((p) => p.isStale).length;
  const pasted = session.photos.filter((p) => p.pastedAtCashier).length;
  const enough = count >= session.minPhotos;

  return (
    <Panel title="Фото">
      <div className="photo-qr">
        {/* Белое поле вокруг кода обязательно: без него камера не распознаёт QR на тёмной теме. */}
        <QRCodeSVG value={session.uploadUrl} size={148} marginSize={3} bgColor="#ffffff" fgColor="#000000" />
        <div className="photo-status">
          <p className={`photo-count tabular ${enough ? 'value-positive' : ''}`}>
            {count} из {session.minPhotos}
          </p>
          <p className="muted">
            {enough
              ? 'Фото достаточно. Можно добавить ещё.'
              : 'Снимки экрана вставьте кнопкой ниже. Чек терминала снимите телефоном: наведите камеру на код.'}
          </p>
          {pasted > 0 && <p className="muted">Вставлено с кассы: {pasted}.</p>}
          {stale > 0 && (
            <p className="photo-stale">Старых или без времени съёмки: {stale}. Они будут отмечены в отчёте.</p>
          )}
        </div>
      </div>
      <div className="photo-actions">
        <Button disabled={busy} onClick={onPaste}>
          Вставить фото
        </Button>
        <Button variant="ghost" disabled={busy} onClick={onCancel}>
          Сбросить фото
        </Button>
      </div>
    </Panel>
  );
}
