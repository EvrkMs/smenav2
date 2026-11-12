import { useEffect, useState } from 'react';
import { api, ApiError, setUnauthorizedHandler } from './api/client';
import { Button, TextField } from './components/ui/primitives';
import Overview from './pages/Overview';
import Operations from './pages/Operations';
import Receipts from './pages/Receipts';
import Employees from './pages/Employees';
import Money from './pages/Money';
import Categories from './pages/Categories';
import Settings from './pages/Settings';
import './App.css';

const tabs = [
  { key: 'overview', label: 'Обзор', component: Overview },
  { key: 'operations', label: 'Операции', component: Operations },
  { key: 'receipts', label: 'Зачисления в банк', component: Receipts },
  { key: 'employees', label: 'Сотрудники', component: Employees },
  { key: 'money', label: 'Банк и остатки', component: Money },
  { key: 'categories', label: 'Категории', component: Categories },
  { key: 'settings', label: 'Настройки', component: Settings },
] as const;

type TabKey = (typeof tabs)[number]['key'];

export default function App() {
  // undefined — ещё не знаем, вошли ли; null — не вошли.
  const [user, setUser] = useState<string | null | undefined>(undefined);

  useEffect(() => {
    setUnauthorizedHandler(() => setUser(null));
    api
      .me()
      .then((me) => setUser(me.username))
      .catch(() => setUser(null));
  }, []);

  if (user === undefined) return null;
  if (user === null) return <Login onLoggedIn={setUser} />;
  return <Workspace user={user} onLoggedOut={() => setUser(null)} />;
}

function Login({ onLoggedIn }: { onLoggedIn: (username: string) => void }) {
  const [username, setUsername] = useState('root');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (!password) return;
    setBusy(true);
    setError('');
    try {
      const me = await api.login(username, password);
      onLoggedIn(me.username);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Не удалось войти.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="token-gate">
      <form
        className="token-gate-card"
        onSubmit={(e) => {
          e.preventDefault();
          void submit();
        }}
      >
        <h3>Смена — панель</h3>
        <TextField label="Имя" value={username} autoComplete="username" onChange={(e) => setUsername(e.target.value)} />
        <TextField
          label="Пароль"
          type="password"
          value={password}
          autoFocus
          autoComplete="current-password"
          onChange={(e) => setPassword(e.target.value)}
        />
        {error && <p className="muted value-negative hint" style={{ marginTop: 0 }}>{error}</p>}
        <Button type="submit" disabled={busy || !password}>
          {busy ? 'Вхожу…' : 'Войти'}
        </Button>
      </form>
    </div>
  );
}

function Workspace({ user, onLoggedOut }: { user: string; onLoggedOut: () => void }) {
  const [active, setActive] = useState<TabKey>('overview');

  const logout = async () => {
    await api.logout().catch(() => undefined);
    onLoggedOut();
  };

  // В отличие от кассы, вкладка создаётся заново при каждом открытии: в панели
  // важнее видеть свежие данные, чем сохранить недозаполненную форму.
  const Active = tabs.find((t) => t.key === active)!.component;

  return (
    <div className="app-shell">
      <header className="app-header">
        <span className="app-title">Смена — панель</span>
        <div className="app-header-right">
          <span className="muted">{user}</span>
          <Button variant="ghost" onClick={logout}>
            Выйти
          </Button>
        </div>
      </header>
      <div className="app-body">
        <nav className="tabs">
          {tabs.map((t) => (
            <button key={t.key} className={t.key === active ? 'tab active' : 'tab'} onClick={() => setActive(t.key)}>
              {t.label}
            </button>
          ))}
        </nav>
        <main className="tab-content">
          <Active />
        </main>
      </div>
    </div>
  );
}
