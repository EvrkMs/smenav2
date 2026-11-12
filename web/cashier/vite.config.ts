import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Интерфейс кассы раздаёт сам сервер по адресу /cashier/: сборка кладётся в его
// wwwroot. Так интерфейс обновляется вместе с сервером, а программу на кассе
// переустанавливать не нужно.
export default defineConfig({
  plugins: [react()],
  base: '/cashier/',
  build: {
    outDir: '../../src/Smena.Server/wwwroot/cashier',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    // При разработке: интерфейс с Vite, API — с локального сервера.
    proxy: { '/api': 'http://127.0.0.1:5080' },
    // Проект лежит на диске Windows, смонтированном в WSL: события файловой системы туда не доходят.
    watch: { usePolling: true },
  },
})
