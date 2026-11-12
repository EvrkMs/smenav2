import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Кабинет сотрудника (Telegram MiniApp) раздаёт сам сервер по адресу /miniapp/:
// сборка кладётся в его wwwroot.
export default defineConfig({
  plugins: [react()],
  base: '/miniapp/',
  build: {
    outDir: '../../src/Smena.Server/wwwroot/miniapp',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5175,
    // При разработке: интерфейс с Vite, API — с локального сервера.
    proxy: { '/api': 'http://127.0.0.1:5080' },
    // Проект лежит на диске Windows, смонтированном в WSL: события файловой системы туда не доходят.
    watch: { usePolling: true },
  },
})
