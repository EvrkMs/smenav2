import { useEffect, useMemo, useRef, useState } from 'react';
import { calculateShift, closeShift, type ShiftCalculation, type ShiftInput } from '../api/client';
import { useCall, useRequestKey } from '../api/hooks';
import { refreshEmployees, setSafe, useConfig, useEmployees, useSafe } from '../lib/appData';
import { Button, NumberField, Panel, Select, TextField } from '../components/ui/primitives';
import { useConfirm } from '../components/ui/ConfirmDialog';
import { useToast } from '../components/ui/Toast';
import { formatMoney, parseIntSafe } from '../lib/format';
import { usePersistedState } from '../lib/hooks';
import { usePhotoSession } from '../lib/photoSession';
import PhotoPanel from '../components/PhotoPanel';
import PastePhotoDialog from '../components/PastePhotoDialog';

interface Row {
  employeeId: string;
  hours: string;
  minus: string;
}

const emptyRow: Row = { employeeId: '', hours: '', minus: '' };

const MAX_AMOUNT_DIGITS = 6;
const MAX_HOURS_DIGITS = 2;

/** Пауза после последнего нажатия клавиши, прежде чем просить у сервера предпросмотр. */
const PREVIEW_DELAY_MS = 300;

const formatSigned = (value: number) => (value > 0 ? `+${formatMoney(value)}` : formatMoney(value));

const signClass = (value: number) => (value < 0 ? 'value-negative' : value > 0 ? 'value-positive' : '');

export default function Raport() {
  const toast = useToast();
  const confirm = useConfirm();
  const call = useCall();

  // Сотрудники, сейф и настройки — из общего store (lib/appData): вкладка не
  // размонтируется, и локальная загрузка «на mount» здесь устаревала бы.
  const employees = useEmployees();
  const config = useConfig();
  const safe = useSafe();

  // Введённое переживает перезапуск программы: смену заполняют не за один присест.
  const [rows, setRows] = usePersistedState<Row[]>('raport_rows', [{ ...emptyRow }]);
  const [factCash, setFactCash] = usePersistedState('raport_factCash', '');
  const [factNonCash, setFactNonCash] = usePersistedState('raport_factNonCash', '');
  const [skladCash, setSkladCash] = usePersistedState('raport_skladCash', '');
  const [skladNonCash, setSkladNonCash] = usePersistedState('raport_skladNonCash', '');
  const [langameCash, setLangameCash] = usePersistedState('raport_langameCash', '');
  const [langameNonCash, setLangameNonCash] = usePersistedState('raport_langameNonCash', '');
  const [factSafe, setFactSafe] = usePersistedState('raport_factSafe', '');
  const [reason, setReason] = usePersistedState('raport_reason', '');

  const [calculation, setCalculation] = useState<ShiftCalculation | null>(null);
  const [busy, setBusy] = useState(false);

  // Фото снимают с телефона до отправки отчёта; к смене их привязывает сервер.
  const photos = usePhotoSession();
  const [photoBusy, setPhotoBusy] = useState(false);
  const photoCount = photos.session?.photos.length ?? 0;

  const activeRows = rows.filter((r) => r.employeeId);
  const totalHours = activeRows.reduce((sum, r) => sum + parseIntSafe(r.hours), 0);

  // То, что уйдёт на сервер. Все расчёты делает он: здесь только сбор ввода.
  const input = useMemo<ShiftInput>(
    () => ({
      factCash: parseIntSafe(factCash),
      factNonCash: parseIntSafe(factNonCash),
      skladCash: parseIntSafe(skladCash),
      skladNonCash: parseIntSafe(skladNonCash),
      langameCash: parseIntSafe(langameCash),
      langameNonCash: parseIntSafe(langameNonCash),
      factSafe: parseIntSafe(factSafe),
      shortageReason: reason.trim() || null,
      employees: rows
        .filter((r) => r.employeeId)
        .map((r) => ({ employeeId: r.employeeId, hours: parseIntSafe(r.hours), minus: parseIntSafe(r.minus) })),
      photoSessionId: photos.sessionId,
    }),
    [factCash, factNonCash, skladCash, skladNonCash, langameCash, langameNonCash, factSafe, reason, rows, photos.sessionId],
  );

  const [requestKey, renewRequestKey] = useRequestKey(input);

  // В закрытие не отдаём причину, если недостачи нет: поле скрыто, и сервер
  // не должен её учитывать. Для предпросчёта исходный ввод остаётся прежним —
  // это не зацикливает пересчёт и не теряет текст, если недостача появится снова.
  const closeInput = useMemo<ShiftInput>(
    () => ({ ...input, shortageReason: calculation?.requiredMinus === 0 ? null : input.shortageReason }),
    [input, calculation],
  );

  // Нехватка фото — единственная проблема, которую администратор решает прямо
  // из кнопки закрытия: нажатие открывает окно с QR и вставкой.
  const photoMissing = calculation?.problems.some((p) => p.code === 'NotEnoughPhotos') ?? false;

  // Предпросмотр считает сервер — теми же формулами, что и закрытие. Ответы
  // могут прийти не по порядку (быстрый ввод), поэтому берём только последний.
  // Баланс сейфа в зависимостях: изменился сейф — изменилось и расхождение.
  // Число фото — тоже: загрузили с телефона ещё одно, и смену уже можно закрыть.
  const previewSeq = useRef(0);
  useEffect(() => {
    const seq = ++previewSeq.current;
    const timer = window.setTimeout(() => {
      calculateShift(input)
        .then((result) => {
          if (seq === previewSeq.current) setCalculation(result);
        })
        .catch(() => {
          // Связи нет: предпросмотр пропадает, кнопка закрытия блокируется.
          if (seq === previewSeq.current) setCalculation(null);
        });
    }, PREVIEW_DELAY_MS);
    return () => window.clearTimeout(timer);
  }, [input, safe, photoCount]);

  const handlePhotoStart = async () => {
    setPhotoBusy(true);
    await call(photos.start());
    setPhotoBusy(false);
  };

  // Окно вставки открывается на конкретную сессию: если её ещё нет, сначала создаём.
  const [pasteSessionId, setPasteSessionId] = useState<string | null>(null);
  // true — окно открыто самим закрытием смены: в нём есть и QR, и вставка.
  const [photoGateOpen, setPhotoGateOpen] = useState(false);

  const closePhotoDialog = () => {
    setPasteSessionId(null);
    setPhotoGateOpen(false);
  };

  const openPhotoDialog = async (fromClose: boolean) => {
    setPhotoBusy(true);
    const id = await call(photos.ensure());
    setPhotoBusy(false);
    if (!id) return;
    setPasteSessionId(id);
    if (fromClose) {
      setPhotoGateOpen(true);
      // Сессия могла быть загружена из localStorage, но ещё не считана с сервера.
      void photos.reload();
    }
  };

  const handlePhotoPaste = () => openPhotoDialog(false);

  const handlePhotoCancel = async () => {
    if (photoCount > 0) {
      const ok = await confirm({
        title: 'Сбросить фото',
        message: `Загруженные фото (${photoCount}) будут удалены, их придётся снять заново. Продолжить?`,
        confirmLabel: 'Сбросить',
      });
      if (!ok) return;
    }
    setPhotoBusy(true);
    await call(photos.cancel());
    setPhotoBusy(false);
  };

  const updateRow = (index: number, patch: Partial<Row>) => {
    setRows((prev) => {
      const next = [...prev];
      next[index] = { ...next[index], ...patch };
      const activeCount = next.filter((r) => r.employeeId).length;
      if (
        patch.employeeId !== undefined &&
        index === next.length - 1 &&
        activeCount === next.length &&
        next.length < config.maxEmployees
      ) {
        next.push({ ...emptyRow });
      }
      if (patch.employeeId === '') return next.slice(0, index + 1);
      return next;
    });
  };

  const handleHoursChange = (index: number, value: string) => {
    const candidateTotal = activeRows.reduce(
      (sum, r, i) => sum + (i === index ? parseIntSafe(value) : parseIntSafe(r.hours)),
      0,
    );
    if (candidateTotal > config.maxTotalHours) {
      toast('error', `Суммарно часов не может быть больше ${config.maxTotalHours}.`);
      return;
    }
    updateRow(index, { hours: value });
  };

  const clearForm = () => {
    setRows([{ ...emptyRow }]);
    setFactCash('');
    setFactNonCash('');
    setSkladCash('');
    setSkladNonCash('');
    setLangameCash('');
    setLangameNonCash('');
    setFactSafe('');
    setReason('');
  };

  const handleSend = async () => {
    if (!calculation) return toast('error', 'Нет связи с сервером — расчёт смены недоступен.');
    if (!calculation.canClose) {
      // Не хватает фото — сразу открываем окно: там и вставка с кассы, и QR для телефона.
      if (calculation.problems.some((p) => p.code === 'NotEnoughPhotos')) {
        await openPhotoDialog(true);
        return;
      }
      return toast('error', calculation.problems[0]?.message ?? 'Смену нельзя закрыть.');
    }

    // Пустое поле и введённый 0 для расчётов неотличимы, поэтому забытое поле
    // молча ушло бы на сервер нулём. Не блокируем (бывают смены, где 0 — правда),
    // но требуем явного подтверждения.
    const emptyFields = [
      [factCash, 'Факт нал.'],
      [factNonCash, 'Факт безнал.'],
      [skladCash, 'МойСклад нал.'],
      [skladNonCash, 'МойСклад безнал.'],
      [langameCash, 'LanGame нал.'],
      [langameNonCash, 'LanGame безнал.'],
      [factSafe, 'Факт в сейфе'],
    ]
      .filter(([value]) => value === '')
      .map(([, label]) => label);
    if (emptyFields.length > 0) {
      const okEmpty = await confirm({
        title: 'Пустые поля',
        message: `Не заполнено: ${emptyFields.join(', ')}. Эти значения уйдут в отчёт как 0. Продолжить?`,
      });
      if (!okEmpty) return;
    }

    // Расхождение по сейфу подтверждается отдельно: администратор должен явно
    // увидеть цифру, а не просто нажать общее «Отправить отчёт?».
    if (calculation.safeDiscrepancy !== 0) {
      const okSafe = await confirm({
        title: 'Расхождение по сейфу',
        message: `Обнаружено расхождение по сейфу: ${formatSigned(calculation.safeDiscrepancy)} ₽. Продолжить закрытие смены?`,
      });
      if (!okSafe) return;
    }

    const ok = await confirm({
      title: 'Закрытие смены',
      message: `Выручка ${formatMoney(calculation.revenue)} ₽, итого ${formatMoney(calculation.total)} ₽. Закрыть смену?`,
      confirmLabel: 'Закрыть смену',
    });
    if (!ok) return;

    setBusy(true);
    const result = await call(closeShift(closeInput, requestKey));
    setBusy(false);
    if (!result) return;

    if (!result.closed) {
      // Между предпросмотром и отправкой что-то изменилось (например, сейф).
      setCalculation(result.calculation);
      return toast('error', result.calculation.problems[0]?.message ?? 'Смена не закрыта.');
    }

    toast('success', 'Смена закрыта.');
    setSafe(calculation.safeAfter);
    photos.forget();
    clearForm();
    renewRequestKey();
    void refreshEmployees().catch(() => undefined);
  };

  return (
    <div className="screen-grid raport-grid">
      <div className="left-column">
        <Panel title="Сотрудники смены">
          {rows.map((row, i) => (
            <div className="raport-row" key={i}>
              <Select label={`Сотрудник ${i + 1}`} value={row.employeeId} onChange={(e) => updateRow(i, { employeeId: e.target.value })}>
                <option value="">— не выбран —</option>
                {employees
                  // Один человек — одна строка смены: уже выбранные в других строках
                  // не предлагаются (свой текущий выбор остаётся, иначе Select «слетит»).
                  .filter((e) => e.id === row.employeeId || !rows.some((r, ri) => ri !== i && r.employeeId === e.id))
                  .map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.name}
                    </option>
                  ))}
              </Select>
              <NumberField label="Часы" value={row.hours} maxDigits={MAX_HOURS_DIGITS} onValueChange={(v) => handleHoursChange(i, v)} disabled={!row.employeeId} />
              <NumberField label="Минус" value={row.minus} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={(v) => updateRow(i, { minus: v })} disabled={!row.employeeId} />
            </div>
          ))}
          <p className="muted tabular" style={{ marginTop: 4 }}>
            Часов по смене: {totalHours} / {config.maxTotalHours}
          </p>
        </Panel>

        <PhotoPanel
          minPhotos={config.minPhotos}
          loading={photos.sessionId !== null && photos.session === null}
          session={photos.session}
          busy={photoBusy || busy}
          onStart={handlePhotoStart}
          onPaste={handlePhotoPaste}
          onCancel={handlePhotoCancel}
        />
        {pasteSessionId !== null && pasteSessionId === photos.sessionId && (
          <PastePhotoDialog
            sessionId={pasteSessionId}
            session={photoGateOpen ? photos.session : null}
            count={photoCount}
            minPhotos={photos.session?.minPhotos ?? config.minPhotos}
            onChanged={() => void photos.reload()}
            onClose={closePhotoDialog}
          />
        )}
      </div>

      <Panel title="Касса">
        <p className="field-group-title">По факту</p>
        <div className="field-pair">
          <NumberField label="Наличные, ₽" value={factCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setFactCash} />
          <NumberField label="Безнал, ₽" value={factNonCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setFactNonCash} />
        </div>

        <p className="field-group-title">LanGame</p>
        <div className="field-pair">
          <NumberField label="Наличные, ₽" value={langameCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setLangameCash} />
          <NumberField label="Безнал, ₽" value={langameNonCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setLangameNonCash} />
        </div>

        <p className="field-group-title">МойСклад</p>
        <div className="field-pair">
          <NumberField label="Наличные, ₽" value={skladCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setSkladCash} />
          <NumberField label="Безнал, ₽" value={skladNonCash} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setSkladNonCash} />
        </div>

        <p className="field-group-title">Сейф</p>
        <NumberField label="Факт в сейфе, ₽" value={factSafe} maxDigits={MAX_AMOUNT_DIGITS} onValueChange={setFactSafe} />

        {calculation && calculation.requiredMinus > 0 && (
          <TextField label="Причина недостачи" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} />
        )}
      </Panel>

      <Panel title="Предпросмотр">
        {calculation ? (
          <>
            <dl className="preview-list">
              <div>
                <dt>Выручка</dt>
                <dd className="tabular">{formatMoney(calculation.revenue)} ₽</dd>
              </div>
              <div>
                <dt>ЗП к выплате</dt>
                <dd className="tabular">{formatMoney(calculation.payrollNet)} ₽</dd>
              </div>
              <div className="preview-total">
                <dt>Итого</dt>
                <dd className="tabular">{formatMoney(calculation.total)} ₽</dd>
              </div>
              <div>
                <dt>По программам</dt>
                <dd className="tabular">{formatMoney(calculation.programTotal)} ₽</dd>
              </div>
              <div>
                <dt>Расхождение по кассе</dt>
                <dd className={`tabular ${signClass(calculation.cashDiscrepancy)}`}>{formatSigned(calculation.cashDiscrepancy)} ₽</dd>
              </div>
              <div>
                <dt>Расхождение по сейфу</dt>
                <dd className={`tabular ${signClass(calculation.safeDiscrepancy)}`}>{formatSigned(calculation.safeDiscrepancy)} ₽</dd>
              </div>
              <div>
                <dt>В сейф из кассы</dt>
                <dd className="tabular">{formatSigned(calculation.cashToSafe)} ₽</dd>
              </div>
            </dl>

            {/* Выплаты по сотрудникам — здесь же, а не отдельной панелью слева:
                тогда отчёт на трёх сотрудников с QR помещается в окно без прокрутки. */}
            {calculation.employees.length > 0 && (
              <>
                <p className="field-group-title">К выплате</p>
                <ul className="ledger-list preview-payable">
                  {calculation.employees.map((e) => (
                    <li key={e.employeeId}>
                      <span>{e.name}</span>
                      <span className="tabular">{formatMoney(e.payable)} ₽</span>
                    </li>
                  ))}
                </ul>
              </>
            )}

            {activeRows.length > 0 && (
              <p className={`muted tabular hint ${calculation.enteredMinus !== calculation.requiredMinus ? 'value-negative' : ''}`}>
                Введено минуса: {formatMoney(calculation.enteredMinus)} / требуется {formatMoney(calculation.requiredMinus)}
              </p>
            )}

            {calculation.problems.length > 0 && (
              <ul className="problem-list">
                {calculation.problems.map((p, i) => (
                  <li key={i}>{p.message}</li>
                ))}
              </ul>
            )}
          </>
        ) : (
          <p className="muted">Расчёт недоступен: нет связи с сервером.</p>
        )}

        <Button disabled={busy || !calculation || (!calculation.canClose && !photoMissing)} onClick={handleSend}>
          {busy ? 'Закрываю…' : photoMissing && !calculation?.canClose ? 'Добавить фото' : 'Закрыть смену'}
        </Button>
      </Panel>
    </div>
  );
}
