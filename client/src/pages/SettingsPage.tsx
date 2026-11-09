import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { authApi, pushApi, usersApi } from '../api/endpoints'
import { useAuth } from '../state/auth'
import { fieldError } from '../lib/errors'
import { formatDay, formatTime } from '../lib/format'
import { ErrorNotice, Field, LanguageSwitch } from '../components/ui'
import { t as translate, useT } from '../i18n'

export function SettingsPage() {
  const t = useT()
  const user = useAuth((s) => s.user)!
  const [params] = useSearchParams()
  return (
    <div className="page">
      <h2>{t('settings.title')}</h2>
      {params.get('linked') ? <div className="notice">{t('settings.linked', { provider: params.get('linked')! })}</div> : null}
      {params.get('error') ? <div className="form-error">{t('settings.linkFailed', { code: params.get('error')! })}</div> : null}
      <section className="card">
        <div className="row">
          <h3 style={{ margin: 0 }}>{t('lang.label')}</h3>
          <span className="spacer" />
          <LanguageSwitch />
        </div>
      </section>
      <ProfileCard key={user.displayName + user.username} />
      <PasswordCard />
      <SessionsCard />
      <PushCard />
      <LinkedAccountsCard />
      <DeleteAccountCard />
    </div>
  )
}

function ProfileCard() {
  const t = useT()
  const user = useAuth((s) => s.user)!
  const [displayName, setDisplayName] = useState(user.displayName)
  const [username, setUsername] = useState(user.username)
  const save = useMutation({
    mutationFn: async () => {
      let me = user
      if (displayName !== user.displayName) me = await usersApi.updateProfile(displayName)
      if (username !== user.username) me = await usersApi.changeUsername(username)
      return me
    },
    onSuccess: (me) => useAuth.getState().setUser(me),
  })
  return (
    <section className="card">
      <h3>{t('settings.profile')}</h3>
      <p className="muted">{user.email}</p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          save.mutate()
        }}
      >
        <Field label={t('auth.displayName')} error={fieldError(save.error, 'displayName')}>
          {(id) => <input id={id} className="input" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />}
        </Field>
        <Field label={t('auth.username')} error={fieldError(save.error, 'username')}>
          {(id) => <input id={id} className="input" value={username} onChange={(e) => setUsername(e.target.value)} />}
        </Field>
        {save.error && !fieldError(save.error, 'displayName') && !fieldError(save.error, 'username') ? <ErrorNotice error={save.error} /> : null}
        {save.isSuccess ? <p className="muted">{t('settings.saved')}</p> : null}
        <button className="btn primary" type="submit" disabled={save.isPending}>
          {t('settings.saveProfile')}
        </button>
      </form>
    </section>
  )
}

function PasswordCard() {
  const t = useT()
  const user = useAuth((s) => s.user)!
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const change = useMutation({
    mutationFn: () => authApi.changePassword(user.hasPassword ? current : null, next),
    onSuccess: () => {
      setCurrent('')
      setNext('')
    },
  })
  return (
    <section className="card">
      <h3>{user.hasPassword ? t('settings.changePassword') : t('settings.setPassword')}</h3>
      <p className="muted">{t('settings.passwordInfo')}</p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          change.mutate()
        }}
      >
        {user.hasPassword ? (
          <Field label={t('settings.currentPassword')} error={fieldError(change.error, 'currentPassword')}>
            {(id) => <input id={id} className="input" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} />}
          </Field>
        ) : null}
        <Field label={t('auth.newPassword')} error={fieldError(change.error, 'newPassword')}>
          {(id) => <input id={id} className="input" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} />}
        </Field>
        {change.isSuccess ? <p className="muted">{t('settings.passwordUpdated')}</p> : null}
        <button className="btn primary" type="submit" disabled={change.isPending || !next}>
          {t('settings.updatePassword')}
        </button>
      </form>
    </section>
  )
}

function SessionsCard() {
  const t = useT()
  const queryClient = useQueryClient()
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: authApi.sessions })
  const revoke = useMutation({
    mutationFn: authApi.revokeSession,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['sessions'] }),
  })
  return (
    <section className="card">
      <h3>{t('settings.sessions')}</h3>
      <ErrorNotice error={sessions.error ?? revoke.error} />
      <ul className="list">
        {(sessions.data ?? []).map((s) => (
          <li key={s.id}>
            <span className="spacer" style={{ minWidth: 0 }}>
              {describeAgent(s.userAgent)} {s.current ? <span className="badge">{t('settings.thisDevice')}</span> : null}
              <br />
              <span className="faint" style={{ fontSize: '0.8em' }}>
                {s.ip ?? t('settings.unknownIp')} · {t('settings.active', { time: `${formatDay(s.lastUsedAt)} ${formatTime(s.lastUsedAt)}` })}
              </span>
            </span>
            {!s.current ? (
              <button className="btn small danger" onClick={() => revoke.mutate(s.id)}>
                {t('settings.signOutSession')}
              </button>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  )
}

function describeAgent(agent: string | null): string {
  if (!agent) return translate('settings.unknownDevice')
  const browser = /Edg\//.test(agent) ? 'Edge' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome' : /Safari\//.test(agent) ? 'Safari' : translate('settings.browser')
  const os = /Windows/.test(agent) ? 'Windows' : /Android/.test(agent) ? 'Android' : /iPhone|iPad/.test(agent) ? 'iOS' : /Mac OS/.test(agent) ? 'macOS' : /Linux/.test(agent) ? 'Linux' : ''
  return os ? translate('settings.deviceOn', { browser, os }) : browser
}

function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padded = (base64 + '='.repeat((4 - (base64.length % 4)) % 4)).replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(padded)
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}

/** NFR / NTF-01: Web Push toggle, shown only when the server has VAPID keys and the browser supports it. */
function PushCard() {
  const t = useT()
  const config = useQuery({ queryKey: ['push-config'], queryFn: pushApi.config, staleTime: Infinity })
  const supported = typeof window !== 'undefined' && 'serviceWorker' in navigator && 'PushManager' in window
  const [endpoint, setEndpoint] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    if (!supported) return
    void navigator.serviceWorker.getRegistration().then((reg) => reg?.pushManager.getSubscription()).then((sub) => setEndpoint(sub?.endpoint ?? null))
  }, [supported])

  if (!config.data?.enabled || !supported) return null

  const enable = async () => {
    setError(null)
    try {
      const registration = await navigator.serviceWorker.ready
      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(config.data!.publicKey!),
      })
      await pushApi.subscribe(subscription.toJSON())
      setEndpoint(subscription.endpoint)
    } catch (e) {
      setError(e)
    }
  }
  const disable = async () => {
    const registration = await navigator.serviceWorker.ready
    const subscription = await registration.pushManager.getSubscription()
    if (subscription) {
      await pushApi.unsubscribe(subscription.endpoint).catch(() => undefined)
      await subscription.unsubscribe()
    }
    setEndpoint(null)
  }

  return (
    <section className="card">
      <h3>{t('settings.notifications')}</h3>
      <p className="muted">{t('settings.pushInfo')}</p>
      <ErrorNotice error={error} />
      {endpoint ? (
        <button className="btn" onClick={disable}>
          {t('settings.pushOff')}
        </button>
      ) : (
        <button className="btn primary" onClick={enable}>
          {t('settings.pushOn')}
        </button>
      )}
    </section>
  )
}

function LinkedAccountsCard() {
  const t = useT()
  const providers = useQuery({ queryKey: ['providers'], queryFn: authApi.providers, staleTime: Infinity })
  if (!providers.data?.providers.length) return null
  return (
    <section className="card">
      <h3>{t('settings.linkedAccounts')}</h3>
      <div className="row">
        {providers.data.providers.map((p) => (
          <a key={p} className="btn" href={`/api/v1/auth/external/${p}/login?mode=link`}>
            {t('settings.link', { provider: p })}
          </a>
        ))}
      </div>
    </section>
  )
}

function DeleteAccountCard() {
  const t = useT()
  const user = useAuth((s) => s.user)!
  const navigate = useNavigate()
  const [password, setPassword] = useState('')
  const remove = useMutation({
    mutationFn: () => usersApi.deleteAccount(user.hasPassword ? password : null),
    onSuccess: () => {
      useAuth.getState().signedOut()
      navigate('/login')
    },
  })
  return (
    <section className="card">
      <h3>{t('settings.deleteAccount')}</h3>
      <p className="muted">{t('settings.deleteWarning')}</p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          if (confirm(t('settings.confirmDelete'))) remove.mutate()
        }}
      >
        {user.hasPassword ? (
          <Field label={t('settings.confirmPassword')} error={fieldError(remove.error, 'password')}>
            {(id) => <input id={id} className="input" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />}
          </Field>
        ) : null}
        <button className="btn danger" type="submit" disabled={remove.isPending}>
          {t('settings.deleteButton')}
        </button>
      </form>
    </section>
  )
}
