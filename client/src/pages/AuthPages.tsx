import { useEffect, useRef, useState, type ChangeEvent, type FormEvent, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, Navigate, useLocation, useNavigate, useSearchParams } from 'react-router-dom'
import { authApi } from '../api/endpoints'
import { ApiError, refreshSession } from '../api/http'
import { useAuth } from '../state/auth'
import { useConnectivity } from '../state/connectivity'
import { ErrorNotice, Field, LanguageSwitch } from '../components/ui'
import { fieldError } from '../lib/errors'
import { useT, type TKey } from '../i18n'

function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  const t = useT()
  const waking = useConnectivity((s) => s.waking)
  return (
    <main className="auth">
      <div className="auth-card">
        <div className="row" style={{ marginBottom: 4 }}>
          <h1 className="logo">{t('app.name')}</h1>
          <span className="spacer" />
          <LanguageSwitch />
        </div>
        <p className="tagline">
          {t('app.tagline')} · {title}
        </p>
        {waking ? (
          <div className="notice" role="status">
            {t('app.wakingShort')}
          </div>
        ) : null}
        {children}
      </div>
    </main>
  )
}

function useSubmit<T>(action: () => Promise<T>) {
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      return await action()
    } catch (e) {
      setError(e)
      return undefined
    } finally {
      setBusy(false)
    }
  }
  return { error, busy, submit }
}

const externalErrors = ['account_exists', 'external_failed', 'session_expired', 'account_suspended']

export function LoginPage() {
  const t = useT()
  const navigate = useNavigate()
  const location = useLocation()
  const [params] = useSearchParams()
  const status = useAuth((s) => s.status)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const providers = useQuery({ queryKey: ['providers'], queryFn: authApi.providers, staleTime: Infinity })
  const { error, busy, submit } = useSubmit(async () => {
    const response = await authApi.login(email, password)
    useAuth.getState().signedIn(response)
    navigate((location.state as { from?: string } | null)?.from ?? '/', { replace: true })
  })
  const notVerified = error instanceof ApiError && error.problem.code === 'email_not_verified'
  const externalError = params.get('error')

  if (status === 'authenticated') return <Navigate to="/" replace />
  return (
    <AuthCard title={t('auth.title.signIn')}>
      {externalError ? (
        <div className="form-error">
          {externalErrors.includes(externalError) ? t(`auth.external.${externalError}` as TKey) : t('auth.external.generic')}
        </div>
      ) : null}
      <form onSubmit={submit} noValidate>
        <Field label={t('auth.email')}>
          {(id) => <input id={id} className="input" type="email" autoComplete="email" value={email} onChange={(e) => setEmail(e.target.value)} required />}
        </Field>
        <Field label={t('auth.password')}>
          {(id) => (
            <input id={id} className="input" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          )}
        </Field>
        <ErrorNotice error={error} />
        {notVerified ? <ResendVerification email={email} /> : null}
        <button className="btn primary wide" disabled={busy} type="submit">
          {busy ? t('auth.signingIn') : t('auth.signIn')}
        </button>
      </form>
      {providers.data?.providers.length ? (
        <div className="stack" style={{ marginTop: 16 }}>
          {providers.data.providers.map((p) => (
            <a key={p} className="btn wide" href={`/api/v1/auth/external/${p}/login`}>
              {t('auth.continueWith', { provider: p })}
            </a>
          ))}
        </div>
      ) : null}
      <p className="muted" style={{ marginTop: 20 }}>
        <Link to="/forgot-password">{t('auth.forgot')}</Link> · {t('auth.newHere')} <Link to="/register">{t('auth.createAccountLink')}</Link>
      </p>
    </AuthCard>
  )
}

function ResendVerification({ email }: { email: string }) {
  const t = useT()
  const [sent, setSent] = useState(false)
  return sent ? (
    <div className="notice">{t('auth.resent')}</div>
  ) : (
    <button type="button" className="btn small" style={{ marginBottom: 12 }} onClick={() => authApi.resendVerification(email).finally(() => setSent(true))}>
      {t('auth.resend')}
    </button>
  )
}

export function RegisterPage() {
  const t = useT()
  const [form, setForm] = useState({ email: '', password: '', username: '', displayName: '' })
  const [done, setDone] = useState(false)
  const { error, busy, submit } = useSubmit(async () => {
    await authApi.register(form)
    setDone(true)
  })
  const set = (key: keyof typeof form) => (e: ChangeEvent<HTMLInputElement>) => setForm({ ...form, [key]: e.target.value })

  if (done) {
    return (
      <AuthCard title={t('auth.title.almostThere')}>
        <p>{t('auth.checkInbox', { email: form.email })}</p>
        <Link className="btn" to="/login">
          {t('auth.backToSignIn')}
        </Link>
      </AuthCard>
    )
  }
  return (
    <AuthCard title={t('auth.title.register')}>
      <form onSubmit={submit} noValidate>
        <Field label={t('auth.email')} error={fieldError(error, 'email')}>
          {(id) => <input id={id} className="input" type="email" autoComplete="email" value={form.email} onChange={set('email')} />}
        </Field>
        <Field label={t('auth.usernameHint')} error={fieldError(error, 'username')}>
          {(id) => <input id={id} className="input" autoComplete="username" value={form.username} onChange={set('username')} />}
        </Field>
        <Field label={t('auth.displayName')} error={fieldError(error, 'displayName')}>
          {(id) => <input id={id} className="input" autoComplete="name" value={form.displayName} onChange={set('displayName')} />}
        </Field>
        <Field label={t('auth.passwordHint')} error={fieldError(error, 'password')}>
          {(id) => <input id={id} className="input" type="password" autoComplete="new-password" value={form.password} onChange={set('password')} />}
        </Field>
        {error instanceof ApiError && error.status !== 422 ? <ErrorNotice error={error} /> : null}
        <button className="btn primary wide" disabled={busy} type="submit">
          {busy ? t('auth.creating') : t('auth.createAccount')}
        </button>
      </form>
      <p className="muted" style={{ marginTop: 20 }}>
        {t('auth.haveAccount')} <Link to="/login">{t('auth.signIn')}</Link>
      </p>
    </AuthCard>
  )
}

export function VerifyEmailPage() {
  const t = useT()
  const [params] = useSearchParams()
  const token = params.get('token') ?? ''
  const [state, setState] = useState<'pending' | 'ok' | 'error'>('pending')
  const [error, setError] = useState<unknown>(null)
  const started = useRef(false)
  useEffect(() => {
    if (started.current) return
    started.current = true
    authApi
      .verifyEmail(token)
      .then(() => setState('ok'))
      .catch((e) => {
        setError(e)
        setState('error')
      })
  }, [token])
  return (
    <AuthCard title={t('auth.title.verify')}>
      {state === 'pending' ? <p role="status">{t('auth.confirming')}</p> : null}
      {state === 'ok' ? <p role="status">{t('auth.confirmed')}</p> : null}
      {state === 'error' ? <ErrorNotice error={error} /> : null}
      <Link className="btn" to="/login">
        {t('auth.goToSignIn')}
      </Link>
    </AuthCard>
  )
}

export function ForgotPasswordPage() {
  const t = useT()
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const { error, busy, submit } = useSubmit(async () => {
    await authApi.forgotPassword(email)
    setSent(true)
  })
  return (
    <AuthCard title={t('auth.title.forgot')}>
      {sent ? (
        <p role="status">{t('auth.resetSent')}</p>
      ) : (
        <form onSubmit={submit} noValidate>
          <Field label={t('auth.email')} error={fieldError(error, 'email')}>
            {(id) => <input id={id} className="input" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />}
          </Field>
          <button className="btn primary" disabled={busy} type="submit">
            {t('auth.sendResetLink')}
          </button>
        </form>
      )}
      <p className="muted" style={{ marginTop: 20 }}>
        <Link to="/login">{t('auth.backToSignIn')}</Link>
      </p>
    </AuthCard>
  )
}

export function ResetPasswordPage() {
  const t = useT()
  const [params] = useSearchParams()
  const [password, setPassword] = useState('')
  const [done, setDone] = useState(false)
  const { error, busy, submit } = useSubmit(async () => {
    await authApi.resetPassword(params.get('token') ?? '', password)
    setDone(true)
  })
  return (
    <AuthCard title={t('auth.title.reset')}>
      {done ? (
        <p role="status">{t('auth.passwordChanged')}</p>
      ) : (
        <form onSubmit={submit} noValidate>
          <Field label={t('auth.newPassword')} error={fieldError(error, 'newPassword')}>
            {(id) => <input id={id} className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />}
          </Field>
          {error instanceof ApiError && error.status !== 422 ? <ErrorNotice error={error} /> : null}
          <button className="btn primary" disabled={busy} type="submit">
            {t('auth.setPassword')}
          </button>
        </form>
      )}
      <p className="muted" style={{ marginTop: 20 }}>
        <Link to="/login">{t('auth.goToSignIn')}</Link>
      </p>
    </AuthCard>
  )
}

/** Landing page after OAuth: the server set the refresh cookie, exchange it for an access token. */
export function AuthCallbackPage() {
  const t = useT()
  const navigate = useNavigate()
  useEffect(() => {
    void refreshSession().then((response) => {
      if (!response) navigate('/login?error=external_failed', { replace: true })
      else navigate(response.user.needsProfile ? '/onboarding' : '/', { replace: true })
    })
  }, [navigate])
  return (
    <AuthCard title={t('auth.title.callback')}>
      <p role="status">{t('auth.oneMoment')}</p>
    </AuthCard>
  )
}

export function OnboardingPage() {
  const t = useT()
  const navigate = useNavigate()
  const user = useAuth((s) => s.user)
  const [username, setUsername] = useState('')
  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const { error, busy, submit } = useSubmit(async () => {
    const me = await authApi.completeProfile(username, displayName)
    useAuth.getState().setUser(me)
    navigate('/', { replace: true })
  })
  return (
    <AuthCard title={t('auth.title.onboarding')}>
      <form onSubmit={submit} noValidate>
        <Field label={t('auth.username')} error={fieldError(error, 'username')}>
          {(id) => <input id={id} className="input" value={username} onChange={(e) => setUsername(e.target.value)} />}
        </Field>
        <Field label={t('auth.displayName')} error={fieldError(error, 'displayName')}>
          {(id) => <input id={id} className="input" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />}
        </Field>
        {error instanceof ApiError && error.status !== 422 ? <ErrorNotice error={error} /> : null}
        <button className="btn primary" disabled={busy} type="submit">
          {t('auth.continue')}
        </button>
      </form>
    </AuthCard>
  )
}
