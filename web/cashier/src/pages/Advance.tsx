import { useState } from 'react';
import { payout } from '../api/client';
import { useCall, useRequestKey } from '../api/hooks';
import { Button, NumberField, Panel, Select, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { refreshEmployees, setSafe, useEmployees } from '../lib/appData';
import { formatMoney, parseIntSafe } from '../lib/format';

type Kind = 'advance' | 'salary';

/** Аванс или выплата ЗП. Администратор платит только наличными из сейфа; безнал — у root в панели. */
export default function Advance() {
  const toast = useToast();
  const confirm = useConfirm();
  const call = useCall();
  const employees = useEmployees();

  const [employeeId, setEmployeeId] = useState('');
  const [amount, setAmount] = useState('');
  const [kind, setKind] = useState<Kind>('advance');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  const employee = employees.find((e) => e.id === employeeId);
  const value = parseIntSafe(amount);

  const request = { employeeId, amount: value, isSalary: kind === 'salary', comment: comment.trim() || null };
  const [requestKey, renewRequestKey] = useRequestKey(request);

  const handleSubmit = async () => {
    if (!employee) return toast('error', 'Выберите сотрудника.');
    if (value <= 0) return toast('error', 'Введите сумму больше нуля.');

    const ok = await confirm({
      title: kind === 'salary' ? 'Выдача ЗП' : 'Выдача аванса',
      message: `${employee.name}\n${formatMoney(value)} ₽ наличными из сейфа`,
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(payout(request, requestKey));
    setBusy(false);
    if (!result) return;

    toast('success', 'Выдача проведена.');
    setSafe(result.safeBalance);
    setAmount('');
    setComment('');
    renewRequestKey();
    void refreshEmployees().catch(() => undefined);
  };

  return (
    <div className="screen-grid screen-grid-single">
      <Panel title="Аванс / ЗП">
        <Select label="Сотрудник" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
          <option value="">— выберите —</option>
          {employees.map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
        </Select>

        {employee && (
          <p className={`muted tabular hint ${employee.balance < 0 ? 'value-negative' : ''}`}>
            К выплате: {formatMoney(employee.balance)} ₽
          </p>
        )}

        <NumberField label="Сумма, ₽" value={amount} maxDigits={6} onValueChange={setAmount} />

        <div className="radio-row">
          <label className={`radio-pill ${kind === 'advance' ? 'active' : ''}`}>
            <input type="radio" checked={kind === 'advance'} onChange={() => setKind('advance')} />
            Аванс
          </label>
          <label className={`radio-pill ${kind === 'salary' ? 'active' : ''}`}>
            <input type="radio" checked={kind === 'salary'} onChange={() => setKind('salary')} />
            ЗП
          </label>
        </div>

        <TextField label="Комментарий (необязательно)" value={comment} onChange={(e) => setComment(e.target.value)} />

        <Button disabled={busy} onClick={handleSubmit}>
          {busy ? 'Провожу…' : 'Выдать из сейфа'}
        </Button>
      </Panel>
    </div>
  );
}
