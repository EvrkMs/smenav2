import { useState } from 'react';
import { api, type Category, type CategoryKind } from '../api/client';
import { useCall, useLoad } from '../api/hooks';
import { Button, Panel, Select, TextField } from '../components/ui/primitives';
import { useToast } from '../components/ui/Toast';

const kinds: { kind: CategoryKind; title: string; hint: string }[] = [
  { kind: 'SafeExpense', title: 'Расходы сейфа', hint: 'выбирает администратор на кассе' },
  { kind: 'SafeIncome', title: 'Приходы сейфа', hint: 'выбирает администратор на кассе' },
  { kind: 'BankExpense', title: 'Расходы банка', hint: 'для вкладки «Банк и остатки»' },
  { kind: 'BankIncome', title: 'Приходы банка', hint: 'для вкладки «Банк и остатки»' },
  { kind: 'Expense', title: 'Расходы (P&L)', hint: 'создаются сами вместе с категориями сейфа и банка' },
  { kind: 'Revenue', title: 'Выручка (P&L)', hint: 'создаются сами вместе с категориями сейфа и банка' },
  { kind: 'Employee', title: 'Операции с сотрудником', hint: 'служебные' },
];

/** Какие вторые счета подходят категории: у расхода деньги уходят, у прихода — приходят. */
const counterOptions: Record<string, [string, string][]> = {
  SafeExpense: [
    ['expense', 'Расходы — трата'],
    ['bank', 'Банк — деньги отнесли в банк'],
  ],
  SafeIncome: [
    ['revenue', 'Выручка — поступление'],
    ['bank', 'Банк — деньги сняли с банка'],
  ],
  BankExpense: [
    ['expense', 'Расходы — трата'],
    ['safe', 'Сейф — деньги сняли в сейф'],
  ],
  BankIncome: [
    ['revenue', 'Выручка — поступление'],
    ['safe', 'Сейф — деньги внесли из сейфа'],
  ],
};

/** Справочники категорий. У каждого вида счёта — свой. */
export default function Categories() {
  const { data, reload } = useLoad(() => api.categories(), []);

  return (
    <div className="page-wide">
      <AddCategory onDone={reload} />
      <div className="page-grid">
        {kinds.map(({ kind, title, hint }) => (
          <Panel key={kind} title={title}>
            <p className="muted hint" style={{ marginTop: -8 }}>
              {hint}
            </p>
            <table className="table">
              <tbody>
                {(data ?? [])
                  .filter((c) => c.kind === kind)
                  .map((c) => (
                    <CategoryRow key={c.id} category={c} onChanged={reload} />
                  ))}
              </tbody>
            </table>
          </Panel>
        ))}
      </div>
    </div>
  );
}

function CategoryRow({ category, onChanged }: { category: Category; onChanged: () => void }) {
  const call = useCall();
  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(category.name);

  const save = async (patch: { name: string; isArchived: boolean }) => {
    const result = await call(api.updateCategory(category.id, patch).then(() => true));
    if (result) {
      setEditing(false);
      onChanged();
    }
  };

  return (
    <tr className={category.isArchived ? 'dimmed' : ''}>
      <td>
        {editing ? (
          <TextField value={name} autoFocus onChange={(e) => setName(e.target.value)} aria-label="Название" />
        ) : (
          <>
            {category.name}
            {category.isSystem && <span className="badge">системная</span>}
            {category.counterAccount && (
              <div className="muted">
                → {category.counterAccount}
                {category.counterCategory && category.counterCategory !== category.name && `: ${category.counterCategory}`}
              </div>
            )}
          </>
        )}
      </td>
      <td>
        {!category.isSystem && (
          <div className="row-actions" style={{ justifyContent: 'flex-end' }}>
            {editing ? (
              <Button className="btn-small" onClick={() => save({ name: name.trim(), isArchived: category.isArchived })}>
                Сохранить
              </Button>
            ) : (
              <Button variant="ghost" className="btn-small" onClick={() => setEditing(true)}>
                Переименовать
              </Button>
            )}
            <Button
              variant="ghost"
              className="btn-small"
              onClick={() => save({ name: category.name, isArchived: !category.isArchived })}
            >
              {category.isArchived ? 'Вернуть' : 'В архив'}
            </Button>
          </div>
        )}
      </td>
    </tr>
  );
}

function AddCategory({ onDone }: { onDone: () => void }) {
  const call = useCall();
  const toast = useToast();
  const [kind, setKind] = useState<CategoryKind>('SafeExpense');
  const [name, setName] = useState('');
  const [counter, setCounter] = useState('expense');
  const [busy, setBusy] = useState(false);

  const counters = counterOptions[kind] ?? [];

  const changeKind = (next: CategoryKind) => {
    setKind(next);
    setCounter(counterOptions[next]?.[0][0] ?? '');
  };

  const submit = async () => {
    if (!name.trim()) return toast('error', 'Введите название.');
    setBusy(true);
    const result = await call(api.createCategory({ kind, name: name.trim(), counter: counters.length > 0 ? counter : null }));
    setBusy(false);
    if (result) {
      toast('success', 'Категория добавлена.');
      setName('');
      onDone();
    }
  };

  return (
    <Panel title="Новая категория">
      <div className="inline-fields">
        <Select label="Справочник" value={kind} onChange={(e) => changeKind(e.target.value as CategoryKind)}>
          {kinds
            .filter((k) => counterOptions[k.kind])
            .map((k) => (
              <option key={k.kind} value={k.kind}>
                {k.title}
              </option>
            ))}
        </Select>
        <TextField label="Название" value={name} onChange={(e) => setName(e.target.value)} />
        <Select label="Куда идут деньги с другой стороны" value={counter} onChange={(e) => setCounter(e.target.value)}>
          {counters.map(([value, label]) => (
            <option key={value} value={value}>
              {label}
            </option>
          ))}
        </Select>
      </div>
      <Button disabled={busy} onClick={submit}>
        {busy ? 'Добавляю…' : 'Добавить категорию'}
      </Button>
    </Panel>
  );
}
