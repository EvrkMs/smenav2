import { api } from '../api/client';
import { useLoad } from '../api/hooks';
import { formatMoney } from '../lib/format';

function Stat({ label, value, note, tone }: { label: string; value: string; note?: string; tone?: 'warn' | 'bad' }) {
  return (
    <div className="stat">
      <div className="stat-label">{label}</div>
      <div className={`stat-value ${tone === 'bad' ? 'value-negative' : ''}`}>{value}</div>
      {note && <div className="stat-note">{note}</div>}
    </div>
  );
}

/** Первая страница: где сейчас деньги и что требует внимания. */
export default function Overview() {
  const { data } = useLoad(() => api.overview(), []);

  if (!data) return <p className="muted">Загружаю…</p>;

  return (
    <div className="page-wide">
      <div className="stat-grid">
        <Stat label="Сейф" value={`${formatMoney(data.safe)} ₽`} note="наличные по учёту" />
        <Stat label="Банк" value={`${formatMoney(data.bankConfirmed)} ₽`} note="подтверждено — сверять с выпиской" />
        <Stat
          label="В пути в банк"
          value={`${formatMoney(data.bankInTransit)} ₽`}
          note={data.pendingReceipts > 0 ? `зачислений ждут подтверждения: ${data.pendingReceipts}` : 'ничего не ждёт подтверждения'}
        />
        <Stat label="Должны сотрудникам" value={`${formatMoney(data.owedToEmployees)} ₽`} note="начислено и не выплачено" />
        <Stat
          label="Должны сотрудники"
          value={`${formatMoney(data.employeeDebts)} ₽`}
          note="балансы в минусе"
          tone={data.employeeDebts > 0 ? 'bad' : undefined}
        />
        <Stat
          label="Очередь Telegram"
          value={String(data.outboxPending)}
          note={data.outboxFailed > 0 ? `отклонено Telegram: ${data.outboxFailed}` : 'сообщений ждут отправки'}
          tone={data.outboxFailed > 0 ? 'bad' : undefined}
        />
      </div>
    </div>
  );
}
