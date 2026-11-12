import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { applyTheme, notifyReady, readLaunch } from './telegram';
import './index.css';

// Один раз при запуске: строка входа и тема приходят в адресе страницы.
const launch = readLaunch();
applyTheme(launch.theme);
notifyReady();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App initData={launch.initData} />
  </StrictMode>,
);
