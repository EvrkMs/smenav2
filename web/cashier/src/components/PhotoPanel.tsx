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
  onStart: () => void;
  onCancel: () => void;
}

/**
 * Фото к смене: касса показывает QR, администратор открывает ссылку с телефона
 * и снимает. Здесь видно только, сколько фото уже загружено; сами снимки
 * администратор видит на телефоне.
 */
export default function PhotoPanel({ minPhotos, loading, session, busy, onStart, onCancel }: Props) {
  if (!session) {
    return (
      <Panel title="Фото">
        <p className="muted photo-hint">
          {minPhotos > 0 ? `К смене нужно не меньше ${minPhotos} фото: ` : 'К смене можно приложить фото: '}
          экран LanGame, экран «МойСклад», чек сверки итогов с терминала.
        </p>
        <Button variant="ghost" disabled={busy || loading} onClick={onStart}>
          {loading ? 'Загружаю…' : 'Показать QR для фото'}
        </Button>
      </Panel>
    );
  }

  const count = session.photos.length;
  const stale = session.photos.filter((p) => p.isStale).length;
  const enough = count >= session.minPhotos;

  return (
    <Panel title="Фото">
      <div className="photo-qr">
        {/* Белое поле вокруг кода обязательно: без него камера не распознаёт QR на тёмной теме. */}
        <QRCodeSVG value={session.uploadUrl} size={176} marginSize={3} bgColor="#ffffff" fgColor="#000000" />
        <div className="photo-status">
          <p className={`photo-count tabular ${enough ? 'value-positive' : ''}`}>
            {count} из {session.minPhotos}
          </p>
          <p className="muted">
            {enough
              ? 'Фото достаточно. Можно добавить ещё.'
              : 'Наведите камеру телефона на код и снимите: экран LanGame, экран «МойСклад», чек сверки итогов.'}
          </p>
          {stale > 0 && (
            <p className="photo-stale">Старых или без времени съёмки: {stale}. Они будут отмечены в отчёте.</p>
          )}
        </div>
      </div>
      <Button variant="ghost" disabled={busy} onClick={onCancel}>
        Сбросить фото
      </Button>
    </Panel>
  );
}
