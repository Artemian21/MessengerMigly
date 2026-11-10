import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { useLocale } from './i18n'
import { initSummaryPersistence } from './state/summaryStorage'
import './styles/theme.css'
import './styles/app.css'

document.documentElement.lang = useLocale.getState().locale
initSummaryPersistence()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)

// PWA (NFR-11): offline shell and Web Push. Registration failure is not fatal.
if ('serviceWorker' in navigator && import.meta.env.PROD) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => undefined)
  })
}
