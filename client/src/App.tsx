import { useEffect, type ReactNode } from 'react'
import { QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { refreshSession } from './api/http'
import { useAuth } from './state/auth'
import { useConnectivity } from './state/connectivity'
import { realtime } from './realtime/hub'
import { useLocale, useT } from './i18n'
import {
  AuthCallbackPage,
  ForgotPasswordPage,
  LoginPage,
  OnboardingPage,
  RegisterPage,
  ResetPasswordPage,
  VerifyEmailPage,
} from './pages/AuthPages'
import { ChatsLayout, ChatsHome } from './pages/ChatsLayout'
import { ChatPage } from './pages/ChatPage'
import { DraftChatPage } from './pages/DraftChatPage'
import { NewChatPage } from './pages/NewChatPage'
import { SearchPage } from './pages/SearchPage'
import { SettingsPage } from './pages/SettingsPage'
import { AdminPage } from './pages/AdminPage'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 15_000 },
  },
})

export function App() {
  // Re-mount the routed tree on language change so memoised views and formatters pick up the new locale.
  const locale = useLocale((s) => s.locale)
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <SessionBootstrap>
          <Routes key={locale}>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/register" element={<RegisterPage />} />
            <Route path="/verify-email" element={<VerifyEmailPage />} />
            <Route path="/forgot-password" element={<ForgotPasswordPage />} />
            <Route path="/reset-password" element={<ResetPasswordPage />} />
            <Route path="/auth/callback" element={<AuthCallbackPage />} />
            <Route path="/onboarding" element={<RequireAuth><OnboardingPage /></RequireAuth>} />
            <Route element={<RequireAuth><ChatsLayout /></RequireAuth>}>
              <Route index element={<ChatsHome />} />
              <Route path="/chats/:conversationId" element={<ChatPage />} />
              <Route path="/direct/:peerId" element={<DraftChatPage />} />
              <Route path="/new" element={<NewChatPage />} />
              <Route path="/search" element={<SearchPage />} />
              <Route path="/settings" element={<SettingsPage />} />
              <Route path="/admin" element={<AdminPage />} />
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </SessionBootstrap>
      </BrowserRouter>
    </QueryClientProvider>
  )
}

/** On load the access token is not stored anywhere: restore the session through the refresh cookie. */
function SessionBootstrap({ children }: { children: ReactNode }) {
  const status = useAuth((s) => s.status)
  const client = useQueryClient()
  const waking = useConnectivity((s) => s.waking)
  const t = useT()

  useEffect(() => {
    if (useAuth.getState().status === 'loading') void refreshSession().then((r) => r ?? useAuth.getState().signedOut())
  }, [])

  useEffect(() => {
    if (status === 'authenticated') {
      void realtime.start(client)
    } else if (status === 'anonymous') {
      void realtime.stop()
      client.clear()
    }
  }, [status, client])

  if (status === 'loading') {
    return (
      <div className="auth">
        <div className="auth-card" role="status" aria-live="polite">
          <p className="logo">{t('app.name')}</p>
          <p className="muted">{waking ? t('app.waking') : t('app.connecting')}</p>
        </div>
      </div>
    )
  }
  return <>{children}</>
}

function RequireAuth({ children }: { children: ReactNode }) {
  const { status, user } = useAuth()
  const location = useLocation()
  if (status !== 'authenticated') {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />
  }
  if (user?.needsProfile && location.pathname !== '/onboarding') {
    return <Navigate to="/onboarding" replace />
  }
  return <>{children}</>
}
