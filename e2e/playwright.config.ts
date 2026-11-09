import { defineConfig, devices } from '@playwright/test'

// Runs against an already started stack: `docker compose up -d postgres mailpit`, the API on :5000 and Vite on :5173
// (or the production container on BASE_URL). Mailpit's API is used to read the verification email.
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  retries: 0,
  use: {
    baseURL: process.env.BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    // The SPA picks uk unless the browser prefers English.
    locale: 'uk-UA',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
