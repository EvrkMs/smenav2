import { Panel } from '../components/ui/primitives';
import { useEmployees } from '../lib/appData';
import { formatMoney } from '../lib/format';

/** Список сотрудников со ставками и балансами. Заводит и меняет сотрудников root в панели. */
export default function Employees() {
  const employees = useEmployees();

  return (
    <div className="screen-grid screen-grid-single">
      <Panel title="Сотрудники">
        {employees.length === 0 ? (
          <p className="muted">Список пуст. Сотрудников добавляет root.</p>
        ) : (
          <ul className="ledger-list">
            {employees.map((e) => (
              <li key={e.id}>
                <span>
                  {e.name} <span className="tabular muted">· {e.hourlyRate} ₽/ч</span>
                </span>
                <span className={`tabular ${e.balance < 0 ? 'value-negative' : ''}`}>{formatMoney(e.balance)} ₽</span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}
