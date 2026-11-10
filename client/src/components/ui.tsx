import { useEffect, useId, type ReactNode } from 'react'
import { errorMessage } from '../lib/errors'
import { initials } from '../lib/format'
import { LOCALES, useLocale, useT } from '../i18n'

export function Avatar({ name, online, small }: { name: string; online?: boolean | null; small?: boolean }) {
  return (
    <span className={`avatar${small ? ' small' : ''}`} aria-hidden="true">
      {initials(name)}
      {online ? <span className="dot" /> : null}
    </span>
  )
}

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const titleId = useId()
  const t = useT()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])
  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="row">
          <h3 id={titleId}>{title}</h3>
          <span className="spacer" />
          <button className="btn ghost small icon" onClick={onClose} aria-label={t('common.close')}>
            ✕
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}

export function Field({
  label,
  error,
  children,
}: {
  label: string
  error?: string
  children: (id: string) => ReactNode
}) {
  const id = useId()
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      {children(id)}
      {error ? (
        <span className="field-error" role="alert">
          {error}
        </span>
      ) : null}
    </div>
  )
}

export function ErrorNotice({ error }: { error: unknown }) {
  if (!error) return null
  return (
    <div className="form-error" role="alert">
      {errorMessage(error)}
    </div>
  )
}

/** Switches the UI language (uk / ru); the choice is remembered in this browser. */
export function LanguageSwitch() {
  const t = useT()
  const { locale, setLocale } = useLocale()
  return (
    <div className="segmented" role="group" aria-label={t('lang.label')}>
      {LOCALES.map((code) => (
        <button
          key={code}
          type="button"
          className={code === locale ? 'active' : ''}
          aria-pressed={code === locale}
          title={t(`lang.${code}`)}
          onClick={() => setLocale(code)}
        >
          {code.toUpperCase()}
        </button>
      ))}
    </div>
  )
}
