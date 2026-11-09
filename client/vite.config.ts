/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev: the SPA proxies REST and the SignalR hub to the ASP.NET Core API, so everything stays same-origin.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: false },
      '/hubs': { target: 'http://localhost:5000', ws: true, changeOrigin: false },
      '/health': { target: 'http://localhost:5000' },
      '/docs': { target: 'http://localhost:5000' },
      '/openapi': { target: 'http://localhost:5000' },
      '/scalar': { target: 'http://localhost:5000' },
    },
  },
  build: { outDir: 'dist', sourcemap: false },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
})
