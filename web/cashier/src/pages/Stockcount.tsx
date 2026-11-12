import { useEffect, useMemo, useState } from 'react';
import { getStock, refreshStock, type Stock, type StockItem } from '../api/client';
import { useCall } from '../api/hooks';
import { Button, Panel, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { usePersistedState } from '../lib/hooks';

/** Строка пересчёта. Остаток в ней не хранится: он берётся из свежих данных сервера. */
interface Row {
  id: string;
  /** Название на момент добавления — на случай, если товар исчезнет из «МойСклад». */
  name: string;
  /** То, что насчитал администратор; пусто — ещё не считал (в расчёте это 0). */
  fact: string;
}

const MAX_HITS = 15;
const MIN_QUERY = 2;

/** 12 → «12», 0.75 → «0.75», 1.5000 → «1.5». */
function formatQty(value: number): string {
  return Number.isInteger(value) ? String(value) : value.toFixed(3).replace(/0+$/, '').replace(/\.$/, '');
}

const formatTime = (iso: string) =>
  new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });

/** Только цифры и одна десятичная точка; запятая приводится к точке. */
function cleanQty(input: string): string {
  const raw = input.replace(/,/g, '.').replace(/[^0-9.]/g, '');
  const dot = raw.indexOf('.');
  return dot === -1 ? raw : raw.slice(0, dot + 1) + raw.slice(dot + 1).replace(/\./g, '');
}

/**
 * Буфер обмена. navigator.clipboard работает только по https или на localhost,
 * а касса открывает сервер по http в локальной сети — поэтому есть запасной путь.
 */
async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Пробуем запасной путь ниже.
  }

  const area = document.createElement('textarea');
  area.value = text;
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.append(area);
  area.select();
  try {
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    area.remove();
  }
}

/**
 * Пересчёт склада: администратор набирает список товаров, вводит, сколько
 * насчитал, и получает сводку недостач относительно «МойСклад». Сводка никуда
 * не отправляется — её копируют и передают руководителю.
 */
export default function Stockcount() {
  const toast = useToast();
  const confirm = useConfirm();
  const call = useCall();

  const [stock, setStock] = useState<Stock | null>(null);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState('');
  // Список переживает перезапуск программы: пересчёт делают не за один присест.
  const [rows, setRows] = usePersistedState<Row[]>('stockcount_rows', []);
  const [showResult, setShowResult] = useState(false);

  useEffect(() => {
    let stopped = false;
    getStock()
      .then((loaded) => {
        if (!stopped) setStock(loaded);
      })
      .catch(() => undefined); // нет связи: покажем это ниже, кнопка «Обновить» повторит
    return () => {
      stopped = true;
    };
  }, []);

  const byId = useMemo(() => new Map((stock?.items ?? []).map((item) => [item.id, item])), [stock]);

  const hits = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (q.length < MIN_QUERY || !stock) return [];
    const added = new Set(rows.map((r) => r.id));
    const found: StockItem[] = [];
    for (const item of stock.items) {
      if (added.has(item.id)) continue;
      if (item.name.toLowerCase().includes(q) || item.article?.toLowerCase().includes(q)) {
        found.push(item);
        if (found.length === MAX_HITS) break;
      }
    }
    return found;
  }, [query, stock, rows]);

  const handleRefresh = async () => {
    setLoading(true);
    const loaded = await call(refreshStock());
    setLoading(false);
    if (!loaded) return;
    setStock(loaded);
    setShowResult(false);
    if (loaded.error) toast('error', `${loaded.error} Показаны прежние остатки.`);
    else toast('success', 'Остатки обновлены.');
  };

  const addItem = (item: StockItem) => {
    setRows((prev) => (prev.some((r) => r.id === item.id) ? prev : [...prev, { id: item.id, name: item.name, fact: '' }]));
    setQuery('');
    setShowResult(false);
  };

  const addAllInStock = async () => {
    if (!stock) return;
    const added = new Set(rows.map((r) => r.id));
    const missing = stock.items.filter((item) => item.stock > 0 && !added.has(item.id));
    if (missing.length === 0) return toast('success', 'Все позиции с остатком уже в списке.');

    const ok = await confirm({
      title: 'Добавить из остатков',
      message: `Добавить в список все позиции с остатком больше нуля (${missing.length})?`,
    });
    if (!ok) return;
    setRows((prev) => [...prev, ...missing.map((item) => ({ id: item.id, name: item.name, fact: '' }))]);
    setShowResult(false);
  };

  const clearAll = async () => {
    const ok = await confirm({ title: 'Очистка', message: 'Удалить все позиции из списка вместе с введёнными количествами?' });
    if (!ok) return;
    setRows([]);
    setShowResult(false);
  };

  // Недостачи: насчитали меньше, чем числится. Излишки в сводку не попадают —
  // так было и в старой версии: сводка нужна, чтобы найти пропажу.
  const shortages = useMemo(
    () =>
      rows
        .map((row) => {
          const item = byId.get(row.id);
          if (!item) return null;
          const parsed = Number(row.fact);
          const fact = row.fact === '' || !Number.isFinite(parsed) ? 0 : parsed;
          const diff = fact - item.stock;
          const percent = item.stock > 0 ? (diff / item.stock) * 100 : 0;
          return { id: row.id, name: item.name, diff, percent };
        })
        .filter((r): r is NonNullable<typeof r> => r !== null && r.diff < 0)
        .sort((a, b) => a.diff - b.diff),
    [rows, byId],
  );

  const uncounted = rows.filter((r) => r.fact === '').length;

  if (stock && !stock.enabled) {
    return (
      <div className="screen-grid screen-grid-single">
        <Panel title="Пересчёт склада">
          <p className="muted">Доступ к «МойСклад» на сервере не настроен. Пересчёт недоступен — обратитесь к руководителю.</p>
        </Panel>
      </div>
    );
  }

  return (
    <div className="screen-grid stockcount-grid">
      <div className="left-column">
        <Panel title="Остатки «МойСклад»">
          <p className="muted hint-line">
            {!stock
              ? 'Загружаю остатки…'
              : stock.refreshedAt
                ? `Позиций: ${stock.items.length}. Данные на ${formatTime(stock.refreshedAt)}.`
                : 'Сервер ещё не получил остатки из «МойСклад».'}
          </p>
          {stock?.error && <p className="value-negative hint-line">{stock.error} Показаны прежние остатки.</p>}
          <Button variant="ghost" disabled={loading} onClick={handleRefresh}>
            {loading ? 'Обновляю…' : 'Обновить остатки'}
          </Button>
        </Panel>

        <Panel title="Поиск позиций">
          <TextField label="Название или артикул" value={query} onChange={(e) => setQuery(e.target.value)} />
          {hits.length > 0 && (
            <ul className="search-hits">
              {hits.map((hit) => (
                <li key={hit.id} onClick={() => addItem(hit)}>
                  {hit.name} <span className="muted">ост: {formatQty(hit.stock)}</span>
                </li>
              ))}
            </ul>
          )}
          {query.trim().length >= MIN_QUERY && hits.length === 0 && stock && (
            <p className="muted hint-line">Ничего не найдено или позиция уже в списке.</p>
          )}
          <Button variant="ghost" disabled={!stock || stock.items.length === 0} onClick={addAllInStock}>
            Добавить все остатки
          </Button>
        </Panel>

        {showResult && (
          <Panel title={shortages.length === 0 ? 'Недостач нет ✓' : `Недостачи: ${shortages.length}`}>
            {uncounted > 0 && (
              <p className="value-negative hint-line">Не заполнено позиций: {uncounted}. Они посчитаны как 0.</p>
            )}
            {shortages.length > 0 && (
              <>
                <table className="ledger-table">
                  <thead>
                    <tr>
                      <th>Товар</th>
                      <th>Разница</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shortages.map((r) => (
                      <tr key={r.id}>
                        <td>{r.name}</td>
                        <td className="tabular value-negative">
                          {formatQty(r.diff)} ({r.percent.toFixed(1)}%)
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <Button
                  variant="ghost"
                  style={{ marginTop: 8 }}
                  onClick={async () => {
                    const ok = await copyText(shortages.map((r) => `${r.name}: ${formatQty(r.diff)}`).join('\n'));
                    toast(ok ? 'success' : 'error', ok ? 'Скопировано.' : 'Не удалось скопировать.');
                  }}
                >
                  Скопировать
                </Button>
              </>
            )}
          </Panel>
        )}
      </div>

      <Panel title={`Список (${rows.length})`}>
        {rows.length === 0 ? (
          <p className="muted">Найдите товар по названию или добавьте сразу все позиции с остатком.</p>
        ) : (
          <table className="ledger-table">
            <thead>
              <tr>
                <th>Наименование</th>
                <th>МС</th>
                <th>Факт</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const item = byId.get(row.id);
                return (
                  <tr key={row.id}>
                    <td>
                      {item?.name ?? row.name}
                      {stock && !item && <span className="muted"> — нет в «МойСклад»</span>}
                    </td>
                    <td className="tabular">{item ? formatQty(item.stock) : '—'}</td>
                    <td>
                      <input
                        className="field-input tabular table-input"
                        inputMode="decimal"
                        value={row.fact}
                        placeholder="0"
                        aria-label={`Факт: ${item?.name ?? row.name}`}
                        onChange={(e) => {
                          const fact = cleanQty(e.target.value);
                          setRows((prev) => prev.map((r) => (r.id === row.id ? { ...r, fact } : r)));
                          setShowResult(false);
                        }}
                      />
                    </td>
                    <td>
                      <button
                        className="table-remove"
                        aria-label={`Убрать: ${item?.name ?? row.name}`}
                        onClick={() => {
                          setRows((prev) => prev.filter((r) => r.id !== row.id));
                          setShowResult(false);
                        }}
                      >
                        ×
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
        <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
          <Button disabled={rows.length === 0 || !stock} onClick={() => setShowResult(true)}>
            Рассчитать
          </Button>
          <Button variant="ghost" disabled={rows.length === 0} onClick={clearAll}>
            Очистить
          </Button>
        </div>
      </Panel>
    </div>
  );
}
