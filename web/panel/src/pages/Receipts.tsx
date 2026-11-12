import { useState } from 'react';
import { api, type Receipt } from '../api/client';
import { useCall, useLoad } from '../api/hooks';
import { Button, Checkbox, NumberField, Panel } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { formatDate, formatDateTime, formatMoney, parseIntSafe } from '../lib/format';

/**
 * Зачисления в банк: безнал смен и инкассации. Деньги считаются «в пути», пока
 * root не сверит их с выпиской и не введёт, сколько реально пришло.
 */
export default function Receipts() {
  const [showAll, setShowAll] = useState(false);
  const { data, loading, reload } = useLoad(() => api.receipts(!showAll), [showAll]);

  return (
    <Panel title="Зачисления в банк">
      <div className="toolbar">
        <Checkbox label="Показывать подтверждённые" checked={showAll} onChange={setShowAll} />
      </div>

      {!data ? (
        <p className="muted">{loading ? 'Загружаю…' : 'Нет данных.'}</p>
      ) : data.length === 0 ? (
        <p className="muted">{showAll ? 'Зачислений нет.' : 'Ничего не ждёт подтверждения.'}</p>
      ) : (
        <div className="table-scroll">
          <table className="table">
            <thead>
              <tr>
                <th>Отправлено</th>
                <th>Канал</th>
                <th className="num">Сумма</th>
                <th>Ожидается</th>
                <th className="num">По расчёту</th>
                <th>Подтверждение</th>
              </tr>
            </thead>
            <tbody>
              {data.map((r) => (
                <ReceiptRow key={r.id} receipt={r} onChanged={reload} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}

function ReceiptRow({ receipt, onChanged }: { receipt: Receipt; onChanged: () => void }) {
  const call = useCall();
  const toast = useToast();
  const confirm = useConfirm();
  const [amount, setAmount] = useState(String(receipt.expectedAmount ?? receipt.amount));
  const [busy, setBusy] = useState(false);

  const confirmReceipt = async () => {
    const value = parseIntSafe(amount, -1);
    if (value < 0 || amount === '') return toast('error', 'Введите зачисленную сумму.');

    const fee = receipt.amount - value;
    const ok = await confirm({
      title: 'Подтверждение зачисления',
      message:
        `Отправлено ${formatMoney(receipt.amount)} ₽, зачислено ${formatMoney(value)} ₽.` +
        (fee > 0 ? `\nКомиссия ${formatMoney(fee)} ₽ уйдёт в расходы.` : '\nБез комиссии.'),
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.confirmReceipt(receipt.id, value));
    setBusy(false);
    if (result) {
      toast('success', result.fee > 0 ? `Зачислено. Комиссия ${formatMoney(result.fee)} ₽.` : 'Зачислено без комиссии.');
      onChanged();
    }
  };

  const unconfirm = async () => {
    const ok = await confirm({
      title: 'Отмена подтверждения',
      message: 'Деньги снова будут считаться «в пути», комиссия (если была) отменится.',
      danger: true,
      confirmLabel: 'Отменить подтверждение',
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.unconfirmReceipt(receipt.id).then(() => true));
    setBusy(false);
    if (result) onChanged();
  };

  return (
    <tr>
      <td className="tabular">{formatDateTime(receipt.sentAt)}</td>
      <td>
        {receipt.channel}
        {receipt.note && <div className="muted">{receipt.note}</div>}
      </td>
      <td className="num">{formatMoney(receipt.amount)}</td>
      <td>
        {formatDate(receipt.expectedAt)}
        {receipt.isDue && <span className="badge badge-warn">пора сверить</span>}
      </td>
      <td className="num">{receipt.expectedAmount === null ? '—' : formatMoney(receipt.expectedAmount)}</td>
      <td>
        {receipt.confirmedAt ? (
          <div className="row-actions">
            <span className="tabular">
              {formatMoney(receipt.confirmedAmount ?? 0)} ₽ · {formatDate(receipt.confirmedAt)}
            </span>
            <Button variant="ghost" className="btn-small" disabled={busy} onClick={unconfirm}>
              Отменить
            </Button>
          </div>
        ) : (
          <div className="row-actions">
            <div style={{ width: 120 }}>
              <NumberField value={amount} maxDigits={8} onValueChange={setAmount} aria-label="Зачислено, ₽" />
            </div>
            <Button className="btn-small" disabled={busy} onClick={confirmReceipt}>
              Зачислено
            </Button>
          </div>
        )}
      </td>
    </tr>
  );
}
