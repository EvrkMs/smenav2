import { useEffect, useState } from 'react';
import { getCategories, safeExpense, safeIncome, type Category } from '../api/client';
import { useCall, useRequestKey } from '../api/hooks';
import { Button, NumberField, Panel, Select, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { setSafe, useSafe } from '../lib/appData';
import { formatMoney, parseIntSafe } from '../lib/format';

interface Props {
  direction: 'income' | 'expense';
}

const texts = {
  income: {
    title: 'Приход в сейф',
    amount: 'Сумма прихода, ₽',
    submit: 'Провести приход',
    done: 'Приход проведён.',
    confirm: (amount: string, category: string) => `Добавить ${amount} ₽ в сейф?\n${category}`,
  },
  expense: {
    title: 'Расход из сейфа',
    amount: 'Сумма расхода, ₽',
    submit: 'Провести расход',
    done: 'Расход проведён.',
    confirm: (amount: string, category: string) => `Взять ${amount} ₽ из сейфа?\n${category}`,
  },
} as const;

/**
 * Приход в сейф и расход из сейфа — одна форма. Администратор выбирает
 * категорию; куда идут деньги с другой стороны (банк, расходы, выручка),
 * определяет сама категория на сервере.
 */
export default function SafeMove({ direction }: Props) {
  const toast = useToast();
  const confirm = useConfirm();
  const call = useCall();
  const safe = useSafe();
  const text = texts[direction];

  const [categories, setCategories] = useState<Category[]>([]);
  const [categoryId, setCategoryId] = useState('');
  const [amount, setAmount] = useState('');
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState(false);

  // Категории меняет только root, поэтому достаточно загрузить их при открытии
  // вкладки; при сбое связи повторяем, пока список не придёт.
  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;

    const load = () => {
      getCategories(direction === 'income' ? 'safeIncome' : 'safeExpense')
        .then((list) => {
          if (!cancelled) setCategories(list);
        })
        .catch(() => {
          if (!cancelled) timer = window.setTimeout(load, 5000);
        });
    };

    load();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [direction]);

  const category = categories.find((c) => String(c.id) === categoryId);
  const value = parseIntSafe(amount);

  const request = { categoryId: category?.id ?? 0, amount: value, comment: comment.trim() || null };
  const [requestKey, renewRequestKey] = useRequestKey(request);

  const handleSubmit = async () => {
    if (!category) return toast('error', 'Выберите категорию.');
    if (value <= 0) return toast('error', 'Введите сумму больше нуля.');
    if (category.commentRequired && !request.comment) return toast('error', 'Укажите комментарий: на что потрачено.');

    const ok = await confirm({
      title: text.title,
      message: text.confirm(formatMoney(value), category.name) + (request.comment ? `\n${request.comment}` : ''),
      danger: direction === 'expense',
      confirmLabel: text.submit,
    });
    if (!ok) return;

    setBusy(true);
    const send = direction === 'income' ? safeIncome : safeExpense;
    const result = await call(send(request, requestKey));
    setBusy(false);
    if (!result) return;

    toast('success', text.done);
    setSafe(result.safeBalance);
    setAmount('');
    setComment('');
    renewRequestKey();
  };

  return (
    <div className="screen-grid screen-grid-single">
      <Panel title={text.title}>
        <p className="muted tabular hint" style={{ marginTop: -8 }}>
          Сейчас в сейфе: {safe === null ? '—' : formatMoney(safe)} ₽
        </p>

        <Select label="Категория" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
          <option value="">— выберите —</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </Select>

        <NumberField label={text.amount} value={amount} maxDigits={6} onValueChange={setAmount} />
        <TextField
          label={category?.commentRequired ? 'Комментарий' : 'Комментарий (необязательно)'}
          value={comment}
          onChange={(e) => setComment(e.target.value)}
        />

        <Button variant={direction === 'expense' ? 'danger' : 'primary'} disabled={busy} onClick={handleSubmit}>
          {busy ? 'Провожу…' : text.submit}
        </Button>
      </Panel>
    </div>
  );
}
