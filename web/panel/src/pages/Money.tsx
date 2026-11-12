import { useState } from 'react';
import { api, type Category, type Employee } from '../api/client';
import { useCall, useLoad, useRequestKey } from '../api/hooks';
import { Button, Checkbox, NumberField, Panel, Select, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { formatMoney, parseIntSafe, parseSignedInt } from '../lib/format';

/** Движения по банку, инвентаризация и стартовые остатки. */
export default function Money() {
  const categories = useLoad(() => api.categories(), []);
  const employees = useLoad(() => api.employees(), []);

  return (
    <div className="page-grid">
      <BankMove direction="expense" categories={categories.data ?? []} />
      <BankMove direction="income" categories={categories.data ?? []} />
      <Inventory employees={(employees.data ?? []).filter((e) => e.isActive)} onDone={employees.reload} />
      <Opening employees={employees.data ?? []} onDone={employees.reload} />
    </div>
  );
}

function BankMove({ direction, categories }: { direction: 'income' | 'expense'; categories: Category[] }) {
  const call = useCall();
  const toast = useToast();
  const confirm = useConfirm();
  const [categoryId, setCategoryId] = useState('');
  const [amount, setAmount] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  const kind = direction === 'income' ? 'BankIncome' : 'BankExpense';
  const options = categories.filter((c) => c.kind === kind && !c.isArchived && c.counterAccount !== null);
  const category = options.find((c) => String(c.id) === categoryId);
  const title = direction === 'income' ? 'Приход на банк' : 'Расход с банка';

  const request = { categoryId: category?.id ?? 0, amount: parseIntSafe(amount), comment: comment.trim() || null };
  const [key, renew] = useRequestKey({ direction, ...request });

  const submit = async () => {
    if (!category) return toast('error', 'Выберите категорию.');
    if (request.amount <= 0) return toast('error', 'Введите сумму больше нуля.');

    const ok = await confirm({
      title,
      message: `${formatMoney(request.amount)} ₽ — ${category.name}` + (request.comment ? `\n${request.comment}` : ''),
      danger: direction === 'expense',
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.bankMove(direction, request, key));
    setBusy(false);
    if (result) {
      toast('success', 'Проведено.');
      setAmount('');
      setComment('');
      renew();
    }
  };

  return (
    <Panel title={title}>
      {options.length === 0 ? (
        <p className="muted">
          Нет категорий. Создайте категорию {direction === 'income' ? 'прихода' : 'расхода'} банка на вкладке «Категории».
        </p>
      ) : (
        <>
          <Select label="Категория" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">— выберите —</option>
            {options.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </Select>
          <NumberField label="Сумма, ₽" value={amount} maxDigits={8} onValueChange={setAmount} />
          <TextField label="Комментарий" value={comment} onChange={(e) => setComment(e.target.value)} />
          <Button variant={direction === 'expense' ? 'danger' : 'primary'} disabled={busy} onClick={submit}>
            {busy ? 'Провожу…' : 'Провести'}
          </Button>
        </>
      )}
    </Panel>
  );
}

function Inventory({ employees, onDone }: { employees: Employee[]; onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const confirm = useConfirm();
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [amount, setAmount] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  const total = parseIntSafe(amount);
  // То же правило, что на сервере: сумма увеличивается до делящейся поровну.
  const share = selected.size > 0 && total > 0 ? Math.ceil(total / selected.size) : 0;

  const request = { employeeIds: [...selected], totalAmount: total, comment: comment.trim() || null };
  const [key, renew] = useRequestKey(request);

  const toggle = (id: string, checked: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });

  const submit = async () => {
    if (selected.size === 0) return toast('error', 'Выберите сотрудников.');
    if (total <= 0) return toast('error', 'Введите сумму недостачи.');

    const ok = await confirm({
      title: 'Инвентаризация',
      message: `Недостача ${formatMoney(total)} ₽ на ${selected.size} чел.: по ${formatMoney(share)} ₽ с каждого.`,
      danger: true,
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.inventory(request, key));
    setBusy(false);
    if (result) {
      toast('success', 'Инвентаризация проведена.');
      setAmount('');
      setComment('');
      setSelected(new Set());
      renew();
      onDone();
    }
  };

  return (
    <Panel title="Инвентаризация">
      <NumberField label="Недостача товара, ₽" value={amount} maxDigits={8} onValueChange={setAmount} />
      {employees.map((e) => (
        <Checkbox key={e.id} label={e.name} checked={selected.has(e.id)} onChange={(checked) => toggle(e.id, checked)} />
      ))}
      {share > 0 && (
        <p className="muted tabular hint" style={{ marginTop: 8 }}>
          По {formatMoney(share)} ₽ с каждого, всего {formatMoney(share * selected.size)} ₽
        </p>
      )}
      <TextField label="Комментарий (необязательно)" value={comment} onChange={(e) => setComment(e.target.value)} />
      <Button variant="danger" disabled={busy} onClick={submit}>
        {busy ? 'Провожу…' : 'Списать с сотрудников'}
      </Button>
    </Panel>
  );
}

function Opening({ employees, onDone }: { employees: Employee[]; onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const confirm = useConfirm();
  const [target, setTarget] = useState<'Safe' | 'Bank' | 'Employee'>('Safe');
  const [employeeId, setEmployeeId] = useState('');
  const [amount, setAmount] = useState('');
  const [busy, setBusy] = useState(false);

  const value = target === 'Employee' ? parseSignedInt(amount) : parseIntSafe(amount);
  const request = { target, employeeId: target === 'Employee' ? employeeId || null : null, amount: value, comment: null };
  const [key, renew] = useRequestKey(request);

  const submit = async () => {
    if (value === 0) return toast('error', 'Введите сумму.');
    if (target === 'Employee' && !employeeId) return toast('error', 'Выберите сотрудника.');

    const where = { Safe: 'сейфа', Bank: 'банка', Employee: employees.find((e) => e.id === employeeId)?.name ?? '' }[target];
    const ok = await confirm({ title: 'Стартовый остаток', message: `Остаток ${where}: ${formatMoney(value)} ₽` });
    if (!ok) return;

    setBusy(true);
    const result = await call(api.opening(request, key));
    setBusy(false);
    if (result) {
      toast('success', 'Остаток внесён.');
      setAmount('');
      renew();
      onDone();
    }
  };

  return (
    <Panel title="Стартовые остатки">
      <p className="muted hint" style={{ marginTop: -8 }}>
        Вносятся один раз при переходе на систему: сколько было в сейфе, на счёте и на балансах сотрудников.
      </p>
      <Select label="Чей остаток" value={target} onChange={(e) => setTarget(e.target.value as typeof target)}>
        <option value="Safe">Сейф</option>
        <option value="Bank">Банк</option>
        <option value="Employee">Сотрудник</option>
      </Select>
      {target === 'Employee' && (
        <Select label="Сотрудник" value={employeeId} onChange={(e) => setEmployeeId(e.target.value)}>
          <option value="">— выберите —</option>
          {employees.map((e) => (
            <option key={e.id} value={e.id}>
              {e.name}
            </option>
          ))}
        </Select>
      )}
      {target === 'Employee' ? (
        <TextField
          label="Сумма, ₽ (с минусом — сотрудник должен клубу)"
          className="tabular"
          inputMode="numeric"
          value={amount}
          onChange={(e) => setAmount(e.target.value.replace(/[^\d-]/g, '').slice(0, 9))}
        />
      ) : (
        <NumberField label="Сумма, ₽" value={amount} maxDigits={8} onValueChange={setAmount} />
      )}
      <Button disabled={busy} onClick={submit}>
        {busy ? 'Вношу…' : 'Внести остаток'}
      </Button>
    </Panel>
  );
}
