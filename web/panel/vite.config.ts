import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Панель root раздаёт сам сервер по адресу /panel/: сборка кладётся в его wwwroot.
export default defineConfig({
  plugins: [react()],
  base: '/panel/',
  build: {
    outDir: '../../src/Smena.Server/wwwroot/panel',
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5174,
    // При разработке: интерфейс с Vite, API — с локального сервера.
    proxy: { '/api': 'http://127.0.0.1:5080' },
    // Проект лежит на диске Windows, смонтированном в WSL: события файловой системы туда не доходят.
    watch: { usePolling: true },
  },
})
