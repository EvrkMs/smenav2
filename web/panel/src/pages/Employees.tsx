import { useState } from 'react';
import { api, type Employee } from '../api/client';
import { useCall, useLoad, useRequestKey } from '../api/hooks';
import { Button, Checkbox, NumberField, Panel, Select, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { formatMoney, parseIntSafe, parseOptionalNumber } from '../lib/format';

type Action = 'payout' | 'fine' | 'bonus' | 'edit';

/** Сотрудники: список с балансами, добавление, правка, выплаты, штрафы и премии. */
export default function Employees() {
  const { data, loading, reload } = useLoad(() => api.employees(), []);
  const [selected, setSelected] = useState<{ employee: Employee; action: Action } | null>(null);

  return (
    <div className="page-wide">
      <Panel title="Сотрудники">
        {!data ? (
          <p className="muted">{loading ? 'Загружаю…' : 'Нет данных.'}</p>
        ) : data.length === 0 ? (
          <p className="muted">Сотрудников пока нет. Добавьте первого ниже.</p>
        ) : (
          <div className="table-scroll">
            <table className="table">
              <thead>
                <tr>
                  <th>Имя</th>
                  <th className="num">Ставка, ₽/ч</th>
                  <th className="num">Баланс, ₽</th>
                  <th>Telegram</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((e) => (
                  <tr key={e.id}>
                    <td>
                      {e.name}
                      {!e.isActive && <span className="badge">отключён</span>}
                    </td>
                    <td className="num">{e.hourlyRate}</td>
                    <td className={`num ${e.balance < 0 ? 'value-negative' : ''}`}>{formatMoney(e.balance)}</td>
                    <td className="tabular muted">
                      {e.telegramId}
                      {e.telegramUsername && ` · @${e.telegramUsername}`}
                      {e.salaryThreadId !== null && ` · тема ${e.salaryThreadId}`}
                    </td>
                    <td>
                      <div className="row-actions">
                        {(['payout', 'bonus', 'fine', 'edit'] as const).map((action) => (
                          <Button key={action} variant="ghost" className="btn-small" onClick={() => setSelected({ employee: e, action })}>
                            {{ payout: 'Выплата', bonus: 'Премия', fine: 'Штраф', edit: 'Изменить' }[action]}
                          </Button>
                        ))}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      <div className="page-grid">
        {selected?.action === 'payout' && <PayoutForm key={selected.employee.id} employee={selected.employee} onDone={reload} />}
        {(selected?.action === 'fine' || selected?.action === 'bonus') && (
          <AdjustForm key={selected.employee.id + selected.action} employee={selected.employee} kind={selected.action} onDone={reload} />
        )}
        {selected?.action === 'edit' && <EditForm key={selected.employee.id} employee={selected.employee} onDone={reload} />}
        <AddForm onDone={reload} />
      </div>
    </div>
  );
}

function PayoutForm({ employee, onDone }: { employee: Employee; onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const confirm = useConfirm();
  const [amount, setAmount] = useState('');
  const [source, setSource] = useState<'safe' | 'bank'>('bank');
  const [kind, setKind] = useState<'advance' | 'salary'>('salary');
  const [allowOverdraft, setAllowOverdraft] = useState(false);
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  const request = {
    employeeId: employee.id,
    amount: parseIntSafe(amount),
    fromBank: source === 'bank',
    isSalary: kind === 'salary',
    allowOverdraft,
    comment: comment.trim() || null,
  };
  const [key, renew] = useRequestKey(request);

  const submit = async () => {
    if (request.amount <= 0) return toast('error', 'Введите сумму больше нуля.');
    const ok = await confirm({
      title: 'Выплата',
      message: `${employee.name}: ${formatMoney(request.amount)} ₽ ${request.fromBank ? 'с банка' : 'из сейфа'}`,
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.payout(request, key));
    setBusy(false);
    if (result) {
      toast('success', 'Выплата проведена.');
      setAmount('');
      setComment('');
      renew();
      onDone();
    }
  };

  return (
    <Panel title={`Выплата: ${employee.name}`}>
      <p className={`muted tabular hint ${employee.balance < 0 ? 'value-negative' : ''}`} style={{ marginTop: -8 }}>
        Баланс: {formatMoney(employee.balance)} ₽
      </p>
      <NumberField label="Сумма, ₽" value={amount} maxDigits={8} onValueChange={setAmount} />
      <div className="inline-fields">
        <Select label="Откуда" value={source} onChange={(e) => setSource(e.target.value as typeof source)}>
          <option value="bank">С банка</option>
          <option value="safe">Из сейфа</option>
        </Select>
        <Select label="Вид" value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
          <option value="salary">ЗП</option>
          <option value="advance">Аванс</option>
        </Select>
      </div>
      <Checkbox label="Разрешить больше баланса (сотрудник уйдёт в минус)" checked={allowOverdraft} onChange={setAllowOverdraft} />
      <TextField label="Комментарий (необязательно)" value={comment} onChange={(e) => setComment(e.target.value)} />
      <Button disabled={busy} onClick={submit}>
        {busy ? 'Провожу…' : 'Выплатить'}
      </Button>
    </Panel>
  );
}

function AdjustForm({ employee, kind, onDone }: { employee: Employee; kind: 'fine' | 'bonus'; onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const [amount, setAmount] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  const title = kind === 'fine' ? 'Штраф' : 'Премия';
  const request = { employeeId: employee.id, amount: parseIntSafe(amount), comment: comment.trim() || null };
  const [key, renew] = useRequestKey({ kind, ...request });

  const submit = async () => {
    if (request.amount <= 0) return toast('error', 'Введите сумму больше нуля.');
    if (!request.comment) return toast('error', 'Укажите, за что.');

    setBusy(true);
    const result = await call(kind === 'fine' ? api.fine(request, key) : api.bonus(request, key));
    setBusy(false);
    if (result) {
      toast('success', `${title}: проведено.`);
      setAmount('');
      setComment('');
      renew();
      onDone();
    }
  };

  return (
    <Panel title={`${title}: ${employee.name}`}>
      <NumberField label="Сумма, ₽" value={amount} maxDigits={8} onValueChange={setAmount} />
      <TextField label="За что" value={comment} onChange={(e) => setComment(e.target.value)} />
      <Button variant={kind === 'fine' ? 'danger' : 'primary'} disabled={busy} onClick={submit}>
        {busy ? 'Провожу…' : kind === 'fine' ? 'Оштрафовать' : 'Начислить премию'}
      </Button>
    </Panel>
  );
}

function EditForm({ employee, onDone }: { employee: Employee; onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const [name, setName] = useState(employee.name);
  const [rate, setRate] = useState(String(employee.hourlyRate));
  const [thread, setThread] = useState(employee.salaryThreadId === null ? '' : String(employee.salaryThreadId));
  const [isActive, setIsActive] = useState(employee.isActive);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    const result = await call(
      api
        .updateEmployee(employee.id, {
          name: name.trim(),
          hourlyRate: parseIntSafe(rate),
          isActive,
          salaryThreadId: parseOptionalNumber(thread),
        })
        .then(() => true),
    );
    setBusy(false);
    if (result) {
      toast('success', 'Сохранено.');
      onDone();
    }
  };

  return (
    <Panel title={`Изменить: ${employee.name}`}>
      <TextField label="Имя" value={name} onChange={(e) => setName(e.target.value)} />
      <NumberField label="Ставка, ₽/час" value={rate} maxDigits={6} onValueChange={setRate} />
      <NumberField label="Тема в зарплатном чате Telegram (необязательно)" value={thread} maxDigits={9} onValueChange={setThread} />
      <Checkbox label="Работает (виден на кассе)" checked={isActive} onChange={setIsActive} />
      <p className="muted hint">Ставка меняется только для будущих смен: в закрытых она сохранена.</p>
      <Button disabled={busy} onClick={submit}>
        {busy ? 'Сохраняю…' : 'Сохранить'}
      </Button>
    </Panel>
  );
}

function AddForm({ onDone }: { onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const [name, setName] = useState('');
  const [rate, setRate] = useState('');
  const [telegramId, setTelegramId] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!name.trim()) return toast('error', 'Введите имя.');
    if (!telegramId) return toast('error', 'Введите Telegram ID: по нему сотрудник входит в личный кабинет.');

    setBusy(true);
    const result = await call(
      api.createEmployee({ telegramId: Number(telegramId), name: name.trim(), hourlyRate: parseIntSafe(rate), salaryThreadId: null }),
    );
    setBusy(false);
    if (result) {
      toast('success', 'Сотрудник добавлен.');
      setName('');
      setRate('');
      setTelegramId('');
      onDone();
    }
  };

  return (
    <Panel title="Добавить сотрудника">
      <TextField label="Имя" value={name} onChange={(e) => setName(e.target.value)} />
      <NumberField label="Ставка, ₽/час" value={rate} maxDigits={6} onValueChange={setRate} />
      <NumberField label="Telegram ID" value={telegramId} maxDigits={15} onValueChange={setTelegramId} />
      <Button disabled={busy} onClick={submit}>
        {busy ? 'Добавляю…' : 'Добавить'}
      </Button>
    </Panel>
  );
}
