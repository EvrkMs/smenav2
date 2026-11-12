import { useCallback, useEffect, useMemo, useState } from 'react';
import { ApiError, getMovements, getProfile, PAGE_SIZE, type ApiErrorKind, type Movement, type Profile } from './api';
import { formatDay, formatMoney, formatSigned, formatTime } from './format';

type State =
  | { status: 'loading' }
  | { status: 'error'; kind: ApiErrorKind }
  | { status: 'ready'; profile: Profile; movements: Movement[]; hasMore: boolean };

const ERROR_TEXT: Record<ApiErrorKind, { title: string; hint: string }> = {
  unauthorized: {
    title: 'Нет доступа',
    hint: 'Закройте кабинет и откройте его заново из Telegram. Если не помогло — вас нет в списке сотрудников клуба, напишите руководителю.',
  },
  network: { title: 'Нет связи с сервером', hint: 'Проверьте интернет и попробуйте ещё раз.' },
  server: { title: 'Ошибка сервера', hint: 'Попробуйте ещё раз чуть позже.' },
};

export default function App({ initData }: { initData: string | null }) {
  const [state, setState] = useState<State>({ status: 'loading' });
  const [loadingMore, setLoadingMore] = useState(false);

  const load = useCallback(async () => {
    if (!initData) return;
    try {
      const [profile, movements] = await Promise.all([getProfile(initData), getMovements(initData)]);
      setState({ status: 'ready', profile, movements, hasMore: movements.length === PAGE_SIZE });
    } catch (e) {
      setState({ status: 'error', kind: e instanceof ApiError ? e.kind : 'server' });
    }
  }, [initData]);

  useEffect(() => {
    void load();
  }, [load]);

  // Вернулись в кабинет из чата — показываем свежие цифры.
  useEffect(() => {
    const onVisible = () => {
      if (!document.hidden) void load();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, [load]);

  const loadMore = async () => {
    if (!initData || state.status !== 'ready' || loadingMore) return;
    const last = state.movements.at(-1);
    if (!last) return;

    setLoadingMore(true);
    try {
      const older = await getMovements(initData, last.id);
      setState({ ...state, movements: [...state.movements, ...older], hasMore: older.length === PAGE_SIZE });
    } catch (e) {
      setState({ status: 'error', kind: e instanceof ApiError ? e.kind : 'server' });
    } finally {
      setLoadingMore(false);
    }
  };

  if (!initData) {
    return (
      <Message
        title="Откройте кабинет из Telegram"
        hint="Эта страница работает только внутри Telegram: по кнопке в чате с ботом клуба."
      />
    );
  }

  if (state.status === 'loading') return <Message title="Загружаю…" />;

  if (state.status === 'error') {
    const text = ERROR_TEXT[state.kind];
    return (
      <Message title={text.title} hint={text.hint}>
        <button
          className="button"
          onClick={() => {
            setState({ status: 'loading' });
            void load();
          }}
        >
          Повторить
        </button>
      </Message>
    );
  }

  return (
    <main>
      <BalanceCard profile={state.profile} />
      <History movements={state.movements} />
      {state.hasMore && (
        <button className="button" disabled={loadingMore} onClick={() => void loadMore()}>
          {loadingMore ? 'Загружаю…' : 'Показать ещё'}
        </button>
      )}
    </main>
  );
}

function Message({ title, hint, children }: { title: string; hint?: string; children?: React.ReactNode }) {
  return (
    <main className="message">
      <h1>{title}</h1>
      {hint && <p className="hint">{hint}</p>}
      {children}
    </main>
  );
}

function BalanceCard({ profile }: { profile: Profile }) {
  const { balance } = profile;
  const label = balance > 0 ? 'Клуб должен вам' : balance < 0 ? 'Вы должны клубу' : 'Расчёт закрыт';

  return (
    <section className="card balance">
      <p className="hint">{profile.name}</p>
      <p className={`amount ${balance < 0 ? 'negative' : ''}`}>{formatMoney(balance)}</p>
      <p className="hint">{label}</p>
      <p className="rate">Ставка: {formatMoney(profile.hourlyRate)} в час</p>
    </section>
  );
}

function History({ movements }: { movements: Movement[] }) {
  // Строки уже идут от новых к старым; остаётся разложить их по дням.
  const days = useMemo(() => {
    const result: { day: string; items: Movement[] }[] = [];
    for (const movement of movements) {
      const day = formatDay(movement.occurredAt);
      const last = result.at(-1);
      if (last?.day === day) last.items.push(movement);
      else result.push({ day, items: [movement] });
    }
    return result;
  }, [movements]);

  if (movements.length === 0) {
    return <p className="hint empty">Начислений и выплат пока нет.</p>;
  }

  return (
    <>
      {days.map(({ day, items }) => (
        <section key={day}>
          <h2>{day}</h2>
          <ul className="card list">
            {items.map((movement) => (
              <li key={movement.id}>
                <div className="what">
                  <span>{movement.title}</span>
                  <span className="hint">
                    {formatTime(movement.occurredAt)}
                    {movement.comment ? ` · ${movement.comment}` : ''}
                  </span>
                </div>
                <span className={`sum ${movement.amount < 0 ? 'negative' : 'positive'}`}>{formatSigned(movement.amount)}</span>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </>
  );
}
