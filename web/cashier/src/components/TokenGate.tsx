import { useState } from 'react';
import { saveDeviceToken } from '../api/client';
import { Button, TextField } from './ui/primitives';

/**
 * Ввод токена устройства при работе в обычном браузере. В программе на кассе
 * этот экран не появляется: оболочка передаёт токен сама.
 */
export function TokenGate({ onSaved }: { onSaved: () => void }) {
  const [token, setToken] = useState('');

  const save = () => {
    if (token.trim().length < 32) return;
    saveDeviceToken(token);
    onSaved();
  };

  return (
    <div className="token-gate">
      <div className="token-gate-card">
        <h3>Подключение кассы</h3>
        <p className="muted">Введите токен устройства. Его выдаёт root; он сохранится в этом браузере.</p>
        <TextField
          label="Токен устройства"
          type="password"
          autoFocus
          value={token}
          onChange={(e) => setToken(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && save()}
        />
        <Button disabled={token.trim().length < 32} onClick={save}>
          Подключить
        </Button>
      </div>
    </div>
  );
}
