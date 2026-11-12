import { useState } from 'react';
import { api, type OperationDetail } from '../api/client';
import { useCall, useLoad } from '../api/hooks';
import { Button, Panel, Select, TextField } from '../components/ui/primitives';
import { useToast } from '../components/ui/Toast';
import { formatDateTime, formatMoney, formatSigned, signClass } from '../lib/format';

const typeOptions = [
  ['', 'Все'],
  ['ShiftClose', 'Закрытие смены'],
  ['Payout', 'Выплата сотруднику'],
  ['SafeIncome', 'Приход в сейф'],
  ['SafeExpense', 'Расход из сейфа'],
  ['BankIncome', 'Приход в банк'],
  ['BankExpense', 'Расход с банка'],
  ['BankReceipt', 'Подтверждение зачисления'],
  ['Inventory', 'Инвентаризация'],
  ['Fine', 'Штраф'],
  ['Bonus', 'Премия'],
  ['Opening', 'Стартовые остатки'],
] as const;

/** Журнал операций: что происходило с деньгами, с возможностью отмены. */
export default function Operations() {
  const [type, setType] = useState('');
  const [status, setStatus] = useState<'all' | 'active' | 'cancelled'>('all');
  const [openId, setOpenId] = useState<string | null>(null);

  const { data, loading, reload } = useLoad(
    () => api.operations({ type: type || undefined, cancelled: status === 'all' ? undefined : status === 'cancelled' }),
    [type, status],
  );

  return (
    <Panel title="Операции">
      <div className="toolbar">
        <Select label="Тип" value={type} onChange={(e) => setType(e.target.value)}>
          {typeOptions.map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
        <Select label="Состояние" value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
          <option value="all">Все</option>
          <option value="active">Действующие</option>
          <option value="cancelled">Отменённые</option>
        </Select>
      </div>

      {!data ? (
        <p className="muted">{loading ? 'Загружаю…' : 'Нет данных.'}</p>
      ) : data.length === 0 ? (
        <p className="muted">Операций нет.</p>
      ) : (
        <div className="table-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Когда</th>
                <th>Операция</th>
                <th>Что</th>
                <th className="num">Сумма</th>
                <th>Кто</th>
              </tr>
            </thead>
            <tbody>
              {data.map((o) => (
                <tr key={o.id} className={`clickable ${o.isCancelled ? 'dimmed' : ''}`} onClick={() => setOpenId(o.id)}>
                  <td className="tabular">{formatDateTime(o.occurredAt)}</td>
                  <td>{o.typeName}</td>
                  <td>
                    {o.lines.length === 1 ? o.lines[0].note : `${o.lines.length} движений`}
                    {o.comment && <div className="muted">{o.comment}</div>}
                  </td>
                  <td className="num">{o.lines.length === 1 ? formatMoney(o.lines[0].amount) : '—'}</td>
                  <td>{o.actorName}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {openId && (
        <OperationDrawer
          id={openId}
          onClose={() => setOpenId(null)}
          onChanged={() => {
            setOpenId(null);
            reload();
          }}
        />
      )}
    </Panel>
  );
}

function OperationDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const call = useCall();
  const toast = useToast();
  const { data } = useLoad(() => api.operation(id), [id]);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);

  const cancel = async () => {
    if (!reason.trim()) return toast('error', 'Укажите причину отмены.');
    setBusy(true);
    const result = await call(api.cancelOperation(id, reason).then(() => true));
    setBusy(false);
    if (result) {
      toast('success', 'Операция отменена.');
      onChanged();
    }
  };

  return (
    <div className="detail-backdrop" onClick={onClose}>
      <div className="detail-drawer" onClick={(e) => e.stopPropagation()}>
        {!data ? (
          <p className="muted">Загружаю…</p>
        ) : (
          <>
            <h3>{data.typeName}</h3>
            <p className="muted">
              {formatDateTime(data.occurredAt)} · {data.actorName}
              {data.comment && ` · ${data.comment}`}
            </p>

            {data.cancelledAt && (
              <p className="value-negative">
                Отменена {formatDateTime(data.cancelledAt)} ({data.cancelledBy}): {data.cancelReason}
              </p>
            )}

            {data.shift && <ShiftBlock shift={data.shift} />}

            <p className="section-title">Движения по счетам</p>
            <table className="table">
              <tbody>
                {data.transactions.map((t, i) => (
                  <tr key={i}>
                    <td>
                      {t.note}
                      {t.entries.map((e, j) => (
                        <div key={j} className="muted">
                          {e.account} {e.direction > 0 ? '+' : '−'}
                          {e.category && ` · ${e.category}`}
                        </div>
                      ))}
                    </td>
                    <td className="num">{formatMoney(t.amount)}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            {!data.cancelledAt && (
              <>
                <p className="section-title">Отмена</p>
                <TextField label="Причина отмены" value={reason} onChange={(e) => setReason(e.target.value)} />
                <Button variant="danger" disabled={busy} onClick={cancel}>
                  {busy ? 'Отменяю…' : 'Отменить операцию'}
                </Button>
              </>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function ShiftBlock({ shift }: { shift: NonNullable<OperationDetail['shift']> }) {
  const programTotal = shift.skladCash + shift.skladNonCash + shift.langameCash + shift.langameNonCash;
  const revenue = shift.factCash - shift.cashFloat + shift.factNonCash;

  return (
    <>
      <p className="section-title">Смена</p>
      <dl className="preview-list">
        <div>
          <dt>Факт: наличные / безнал</dt>
          <dd className="tabular">
            {formatMoney(shift.factCash)} / {formatMoney(shift.factNonCash)}
          </dd>
        </div>
        <div>
          <dt>LanGame: наличные / безнал</dt>
          <dd className="tabular">
            {formatMoney(shift.langameCash)} / {formatMoney(shift.langameNonCash)}
          </dd>
        </div>
        <div>
          <dt>МойСклад: наличные / безнал</dt>
          <dd className="tabular">
            {formatMoney(shift.skladCash)} / {formatMoney(shift.skladNonCash)}
          </dd>
        </div>
        <div>
          <dt>По программам</dt>
          <dd className="tabular">{formatMoney(programTotal)}</dd>
        </div>
        <div className="preview-total">
          <dt>Выручка</dt>
          <dd className="tabular">{formatMoney(revenue)}</dd>
        </div>
        <div>
          <dt>Расхождение по кассе</dt>
          <dd className={`tabular ${signClass(shift.cashDiscrepancy)}`}>{formatSigned(shift.cashDiscrepancy)}</dd>
        </div>
        <div>
          <dt>Сейф: факт / по учёту</dt>
          <dd className="tabular">
            {formatMoney(shift.factSafe)} / {formatMoney(shift.expectedSafe)}
          </dd>
        </div>
        <div>
          <dt>Расхождение по сейфу</dt>
          <dd className={`tabular ${signClass(shift.safeDiscrepancy)}`}>{formatSigned(shift.safeDiscrepancy)}</dd>
        </div>
      </dl>
      {shift.shortageReason && <p className="muted">Причина недостачи: {shift.shortageReason}</p>}
      <table className="table">
        <thead>
          <tr>
            <th>Сотрудник</th>
            <th className="num">Часы</th>
            <th className="num">Ставка</th>
            <th className="num">Минус</th>
            <th className="num">К выплате</th>
          </tr>
        </thead>
        <tbody>
          {shift.employees.map((e, i) => (
            <tr key={i}>
              <td>{e.name}</td>
              <td className="num">{e.hours}</td>
              <td className="num">{e.hourlyRate}</td>
              <td className="num">{formatMoney(e.minus)}</td>
              <td className="num">{formatMoney(e.hours * e.hourlyRate - e.minus)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <p className="section-title">Фото ({shift.photos.length})</p>
      {shift.photos.length === 0 ? (
        <p className="muted">К смене фото не приложены.</p>
      ) : (
        <div className="photo-grid">
          {shift.photos.map((photo, i) => (
            // Снимок открывается в новой вкладке в полном размере; cookie панели уходит с запросом сама.
            <a key={photo.id} className="photo-card" href={`/api/root/photos/${photo.id}`} target="_blank" rel="noreferrer">
              <img src={`/api/root/photos/${photo.id}`} alt={`Фото ${i + 1}`} loading="lazy" />
              <span className={photo.isStale ? 'value-negative' : 'muted'}>
                {photo.takenAt
                  ? `${photo.isStale ? 'Старый снимок: ' : 'Снято '}${formatPhotoTime(photo.takenAt)}`
                  : 'Нет времени съёмки'}
              </span>
            </a>
          ))}
        </div>
      )}
    </>
  );
}

const formatPhotoTime = (iso: string) =>
  new Date(iso).toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
