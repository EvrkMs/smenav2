import { useCallback, useEffect, useRef, useState } from 'react';
import { QRCodeSVG } from 'qrcode.react';
import { ApiError, pastePhoto, removePhoto, type PhotoSession } from '../api/client';
import { Button } from './ui/primitives';
import './ui/confirm.css';
import './photoPanel.css';

interface Pasted {
  /** Идентификатор снимка на сервере. */
  id: string;
  /** Локальная ссылка на вставленную картинку — для миниатюры, без запроса к серверу. */
  preview: string;
}

interface Props {
  sessionId: string;
  /**
   * Открыто из «Закрыть смену»: показываем QR рядом со вставкой, чтобы
   * администратор мог сразу снять чек терминала телефоном.
   */
  session?: PhotoSession | null;
  /** Сколько фото уже в сессии (с телефона и с кассы) и сколько нужно. */
  count: number;
  minPhotos: number;
  /** Снимок добавлен или убран: пора перечитать сессию. */
  onChanged: () => void;
  onClose: () => void;
}

/** Картинки из буфера обмена или из перетащенных файлов. */
function imagesFrom(data: DataTransfer | null): File[] {
  if (!data) return [];
  const files: File[] = [];
  for (const item of data.items) {
    if (item.kind === 'file' && item.type.startsWith('image/')) {
      const file = item.getAsFile();
      if (file) files.push(file);
    }
  }
  return files;
}

/**
 * Окно «Вставить фото»: снимок экрана (LanGame, «МойСклад») делается на самой
 * кассе, поэтому снимать монитор телефоном незачем — достаточно вставить его
 * из буфера обмена. Каждый вставленный снимок сразу уходит на сервер.
 */
export default function PastePhotoDialog({ sessionId, session = null, count, minPhotos, onChanged, onClose }: Props) {
  const [pasted, setPasted] = useState<Pasted[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  // Ссылки на миниатюры держат картинки в памяти — освобождаем при закрытии окна.
  const previews = useRef<string[]>([]);
  useEffect(() => () => previews.current.forEach((url) => URL.revokeObjectURL(url)), []);

  const upload = useCallback(
    async (images: File[]) => {
      if (images.length === 0) {
        setError('В буфере обмена нет картинки. Сделайте снимок экрана (Win+Shift+S) и вставьте его сюда.');
        return;
      }

      setBusy(true);
      setError(null);
      for (const image of images) {
        try {
          const photo = await pastePhoto(sessionId, image);
          const preview = URL.createObjectURL(image);
          previews.current.push(preview);
          setPasted((prev) => [...prev, { id: photo.id, preview }]);
          onChanged();
        } catch (e) {
          setError(e instanceof ApiError ? e.message : 'Не удалось загрузить снимок.');
          break;
        }
      }
      setBusy(false);
    },
    [sessionId, onChanged],
  );

  // Ctrl+V ловится на всём окне программы, пока диалог открыт: щёлкать по полю не нужно.
  useEffect(() => {
    const onPaste = (event: ClipboardEvent) => {
      event.preventDefault();
      if (!busy) void upload(imagesFrom(event.clipboardData));
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    document.addEventListener('paste', onPaste);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('paste', onPaste);
      document.removeEventListener('keydown', onKey);
    };
  }, [busy, upload, onClose]);

  const remove = async (item: Pasted) => {
    setBusy(true);
    setError(null);
    try {
      await removePhoto(sessionId, item.id);
      const index = previews.current.indexOf(item.preview);
      if (index >= 0) previews.current.splice(index, 1);
      URL.revokeObjectURL(item.preview);
      setPasted((prev) => prev.filter((p) => p.id !== item.id));
      onChanged();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Не удалось удалить снимок.');
    }
    setBusy(false);
  };

  return (
    <div className="confirm-backdrop" onClick={onClose}>
      <div
        className="confirm-slip paste-dialog"
        role="dialog"
        aria-label={session ? 'Фото к смене' : 'Вставить фото'}
        onClick={(e) => e.stopPropagation()}
        onDragOver={(e) => e.preventDefault()}
        onDrop={(e) => {
          e.preventDefault();
          if (!busy) void upload(imagesFrom(e.dataTransfer));
        }}
      >
        <div className="confirm-slip-perforation" aria-hidden="true" />
        <h4>{session ? 'Фото к смене' : 'Вставить фото'}</h4>

        {session && (
          <div className="photo-qr paste-qr">
            {/* Белое поле вокруг QR обязательно: камера телефона не распознаёт код на тёмной теме. */}
            <QRCodeSVG value={session.uploadUrl} size={148} marginSize={3} bgColor="#ffffff" fgColor="#000000" />
            <div className="photo-status">
              <p className={`photo-count tabular ${count >= minPhotos ? 'value-positive' : ''}`}>
                {count} из {minPhotos}
              </p>
              <p className="muted">Снимите чек терминала телефоном: наведите камеру на код. Снимки экрана вставьте ниже.</p>
            </div>
          </div>
        )}

        <div className={`paste-zone ${busy ? 'busy' : ''}`}>
          <strong>{busy ? 'Загружаю…' : 'Нажмите Ctrl+V'}</strong>
          <span>
            Снимок экрана: Win+Shift+S, выделить область, затем Ctrl+V здесь. Можно перетащить файл или{' '}
            <button type="button" className="paste-link" disabled={busy} onClick={() => fileInput.current?.click()}>
              выбрать его
            </button>
            .
          </span>
          <input
            ref={fileInput}
            type="file"
            accept="image/*"
            multiple
            hidden
            onChange={(e) => {
              const files = [...(e.target.files ?? [])];
              e.target.value = '';
              void upload(files);
            }}
          />
        </div>

        {error && <p className="paste-error">{error}</p>}

        {pasted.length > 0 && (
          <div className="paste-list">
            {pasted.map((item, index) => (
              <figure key={item.id}>
                <img src={item.preview} alt={`Вставленное фото ${index + 1}`} />
                <button type="button" disabled={busy} aria-label={`Удалить фото ${index + 1}`} onClick={() => void remove(item)}>
                  ×
                </button>
              </figure>
            ))}
          </div>
        )}

        <div className="confirm-actions paste-footer">
          <span className={`tabular ${count >= minPhotos ? 'value-positive' : ''}`}>
            Фото в смене: {count} из {minPhotos}
          </span>
          <Button onClick={onClose}>Готово</Button>
        </div>
      </div>
    </div>
  );
}
