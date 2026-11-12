import { useEffect, useState } from 'react';
import { api, type Settings as SettingsData } from '../api/client';
import { useCall, useLoad } from '../api/hooks';
import { Button, NumberField, Panel, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { formatDateTime, parseIntSafe, parseOptionalNumber } from '../lib/format';

/** Настройки клуба, условия зачислений, устройства кассы и пароль. */
export default function Settings() {
  return (
    <div className="page-grid">
      <ClubSettings />
      <Devices />
      <Password />
    </div>
  );
}

/** Все поля формы — строки: так пустое поле отличается от нуля. */
type Form = Record<
  | 'cashFloat'
  | 'maxEmployeesPerShift'
  | 'maxHoursPerShift'
  | 'minShiftPhotos'
  | 'photoMaxAgeHours'
  | 'timeZoneId'
  | 'telegramChatId'
  | 'telegramReportsThreadId'
  | 'telegramSafeThreadId'
  | 'telegramExpensesThreadId'
  | 'telegramSalaryChatId',
  string
>;

const text = (value: number | string | null) => (value === null ? '' : String(value));

function ClubSettings() {
  const call = useCall();
  const toast = useToast();
  const { data, reload } = useLoad(() => api.settings(), []);
  const [form, setForm] = useState<Form | null>(null);
  const [channels, setChannels] = useState<{ id: number; name: string; delayDays: string; feePercent: string }[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!data) return;
    setForm({
      cashFloat: text(data.cashFloat),
      maxEmployeesPerShift: text(data.maxEmployeesPerShift),
      maxHoursPerShift: text(data.maxHoursPerShift),
      minShiftPhotos: text(data.minShiftPhotos),
      photoMaxAgeHours: text(data.photoMaxAgeHours),
      timeZoneId: data.timeZoneId,
      telegramChatId: text(data.telegramChatId),
      telegramReportsThreadId: text(data.telegramReportsThreadId),
      telegramSafeThreadId: text(data.telegramSafeThreadId),
      telegramExpensesThreadId: text(data.telegramExpensesThreadId),
      telegramSalaryChatId: text(data.telegramSalaryChatId),
    });
    setChannels(data.channels.map((c) => ({ id: c.id, name: c.name, delayDays: text(c.delayDays), feePercent: text(c.feePercent) })));
  }, [data]);

  if (!form) return <Panel title="Клуб">Загружаю…</Panel>;

  const set = (key: keyof Form) => (value: string) => setForm({ ...form, [key]: value });

  const save = async () => {
    const body: SettingsData = {
      cashFloat: parseIntSafe(form.cashFloat),
      maxEmployeesPerShift: parseIntSafe(form.maxEmployeesPerShift),
      maxHoursPerShift: parseIntSafe(form.maxHoursPerShift),
      minShiftPhotos: parseIntSafe(form.minShiftPhotos),
      photoMaxAgeHours: parseIntSafe(form.photoMaxAgeHours),
      timeZoneId: form.timeZoneId.trim(),
      telegramChatId: parseOptionalNumber(form.telegramChatId),
      telegramReportsThreadId: parseOptionalNumber(form.telegramReportsThreadId),
      telegramSafeThreadId: parseOptionalNumber(form.telegramSafeThreadId),
      telegramExpensesThreadId: parseOptionalNumber(form.telegramExpensesThreadId),
      telegramSalaryChatId: parseOptionalNumber(form.telegramSalaryChatId),
      channels: channels.map((c) => ({
        id: c.id,
        name: c.name,
        delayDays: parseOptionalNumber(c.delayDays),
        feePercent: parseOptionalNumber(c.feePercent),
      })),
    };

    setBusy(true);
    const result = await call(api.saveSettings(body).then(() => true));
    setBusy(false);
    if (result) {
      toast('success', 'Настройки сохранены.');
      reload();
    }
  };

  // Чаты Telegram отрицательные (−100…), поэтому это текстовые поля, а не «только цифры».
  const signed = (key: keyof Form, label: string) => (
    <TextField label={label} className="tabular" value={form[key]} onChange={(e) => set(key)(e.target.value.replace(/[^\d-]/g, ''))} />
  );

  return (
    <Panel title="Клуб">
      <p className="section-title" style={{ marginTop: 0 }}>
        Смена
      </p>
      <div className="inline-fields">
        <NumberField label="Размен в кассе, ₽" value={form.cashFloat} maxDigits={6} onValueChange={set('cashFloat')} />
        <NumberField label="Сотрудников в смене" value={form.maxEmployeesPerShift} maxDigits={2} onValueChange={set('maxEmployeesPerShift')} />
        <NumberField label="Часов в смене" value={form.maxHoursPerShift} maxDigits={2} onValueChange={set('maxHoursPerShift')} />
        <NumberField label="Минимум фото" value={form.minShiftPhotos} maxDigits={2} onValueChange={set('minShiftPhotos')} />
        <NumberField label="Давность фото, ч" value={form.photoMaxAgeHours} maxDigits={3} onValueChange={set('photoMaxAgeHours')} />
        <TextField label="Часовой пояс" value={form.timeZoneId} onChange={(e) => set('timeZoneId')(e.target.value)} />
      </div>

      <p className="section-title">Зачисления в банк</p>
      {channels.map((c, i) => (
        <div className="inline-fields" key={c.id}>
          <NumberField
            label={`${c.name}: задержка, дней`}
            value={c.delayDays}
            maxDigits={2}
            onValueChange={(v) => setChannels(channels.map((x, j) => (j === i ? { ...x, delayDays: v } : x)))}
          />
          <TextField
            label={`${c.name}: комиссия, %`}
            className="tabular"
            value={c.feePercent}
            onChange={(e) =>
              setChannels(channels.map((x, j) => (j === i ? { ...x, feePercent: e.target.value.replace(/[^\d.,]/g, '') } : x)))
            }
          />
        </div>
      ))}
      <p className="muted hint">Пусто — условие неизвестно. На учёт влияет только фактическая сумма, которую вы вводите при подтверждении.</p>

      <p className="section-title">Telegram (необязательно)</p>
      <div className="inline-fields">
        {signed('telegramChatId', 'Чат администраторов')}
        {signed('telegramReportsThreadId', 'Тема отчётов')}
        {signed('telegramSafeThreadId', 'Тема сейфа')}
        {signed('telegramExpensesThreadId', 'Тема расходов')}
        {signed('telegramSalaryChatId', 'Зарплатный чат')}
      </div>

      <Button disabled={busy} onClick={save}>
        {busy ? 'Сохраняю…' : 'Сохранить настройки'}
      </Button>
    </Panel>
  );
}

function Devices() {
  const call = useCall();
  const confirm = useConfirm();
  const { data, reload } = useLoad(() => api.devices(), []);
  const [name, setName] = useState('');
  const [issued, setIssued] = useState<{ name: string; token: string } | null>(null);

  const create = async () => {
    if (!name.trim()) return;
    const result = await call(api.createDevice(name.trim()));
    if (result) {
      setIssued({ name: result.name, token: result.token });
      setName('');
      reload();
    }
  };

  const revoke = async (id: string, deviceName: string) => {
    const ok = await confirm({
      title: 'Отозвать устройство',
      message: `«${deviceName}» больше не сможет работать с сервером. Вернуть токен нельзя — только выдать новый.`,
      danger: true,
      confirmLabel: 'Отозвать',
    });
    if (ok && (await call(api.revokeDevice(id).then(() => true)))) reload();
  };

  return (
    <Panel title="Устройства кассы">
      <table className="table">
        <tbody>
          {(data ?? []).map((d) => (
            <tr key={d.id} className={d.revokedAt ? 'dimmed' : ''}>
              <td>
                {d.name}
                <div className="muted">выдано {formatDateTime(d.createdAt)}</div>
              </td>
              <td>
                {!d.revokedAt && (
                  <Button variant="ghost" className="btn-small" onClick={() => revoke(d.id, d.name)}>
                    Отозвать
                  </Button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      {issued && (
        <>
          <p className="muted" style={{ marginTop: 14 }}>
            Токен для «{issued.name}». Скопируйте его сейчас — больше он не будет показан:
          </p>
          <div className="token-box">{issued.token}</div>
        </>
      )}

      <p className="section-title">Новое устройство</p>
      <TextField label="Название" value={name} onChange={(e) => setName(e.target.value)} />
      <Button disabled={!name.trim()} onClick={create}>
        Выдать токен
      </Button>
    </Panel>
  );
}

function Password() {
  const call = useCall();
  const toast = useToast();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setBusy(true);
    const result = await call(api.changePassword(current, next).then(() => true));
    setBusy(false);
    if (result) {
      toast('success', 'Пароль изменён.');
      setCurrent('');
      setNext('');
    }
  };

  return (
    <Panel title="Пароль">
      <TextField label="Текущий пароль" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />
      <TextField label="Новый пароль (от 10 символов)" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />
      <Button disabled={busy || !current || next.length < 10} onClick={submit}>
        {busy ? 'Меняю…' : 'Сменить пароль'}
      </Button>
    </Panel>
  );
}
