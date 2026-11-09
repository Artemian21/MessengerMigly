import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { authApi, conversationsApi } from '../api/endpoints'
import type { Conversation } from '../api/types'
import { useAuth } from '../state/auth'
import { useConnectivity } from '../state/connectivity'
import { useLive } from '../state/live'
import { realtime } from '../realtime/hub'
import { conversationsKey } from '../lib/messages'
import { formatListTime } from '../lib/format'
import { conversationTitle } from '../lib/conversations'
import { useT } from '../i18n'
import { Avatar, LanguageSwitch } from '../components/ui'

export function ChatsLayout() {
  const t = useT()
  const location = useLocation()
  const navigate = useNavigate()
  const user = useAuth((s) => s.user)
  const waking = useConnectivity((s) => s.waking)
  const hub = useConnectivity((s) => s.hub)
  const conversations = useQuery({ queryKey: conversationsKey, queryFn: conversationsApi.list })

  useEffect(() => {
    const peers = (conversations.data ?? []).flatMap((c) => (c.peer && !c.peer.isDeleted ? [c.peer.id] : []))
    void realtime.watchPresence(peers)
  }, [conversations.data, hub])

  const signOut = async () => {
    await authApi.logout().catch(() => undefined)
    useAuth.getState().signedOut()
    navigate('/login')
  }

  const view = location.pathname === '/' ? 'list' : 'chat'
  return (
    <div className="shell-wrap">
      {waking ? (
        <div className="banner" role="status">
          {t('app.wakingBanner')}
        </div>
      ) : hub === 'reconnecting' ? (
        <div className="banner" role="status">
          {t('app.reconnecting')}
        </div>
      ) : null}
      <div className="shell" data-view={view}>
        <aside className="sidebar" aria-label={t('nav.conversations')}>
          <div className="sidebar-header">
            <strong className="logo">{t('app.name')}</strong>
            <span className={`live-dot${hub === 'connected' ? ' on' : ''}`} title={t('app.realtime', { state: hub })}>
              {hub === 'connected' ? t('app.live') : t('app.offline')}
            </span>
            <span className="spacer" />
            <LanguageSwitch />
          </div>
          <nav className="sidebar-nav">
            <NavLink className="btn small" to="/new">
              {t('nav.newChat')}
            </NavLink>
            <NavLink className="btn small" to="/search">
              {t('nav.search')}
            </NavLink>
            <NavLink className="btn small" to="/settings">
              @{user?.username}
            </NavLink>
            {user?.role === 'Admin' ? (
              <NavLink className="btn small" to="/admin">
                {t('nav.admin')}
              </NavLink>
            ) : null}
            <button className="btn small ghost" onClick={signOut}>
              {t('nav.signOut')}
            </button>
          </nav>
          {conversations.isLoading ? <p className="empty">{t('list.loading')}</p> : null}
          {conversations.data?.length === 0 ? <p className="empty">{t('list.empty')}</p> : null}
          <ConversationList items={conversations.data ?? []} me={user?.id ?? ''} />
        </aside>
        <section className="main">
          <Outlet />
        </section>
      </div>
    </div>
  )
}

function ConversationList({ items, me }: { items: Conversation[]; me: string }) {
  const t = useT()
  const presence = useLive((s) => s.presence)
  const typing = useLive((s) => s.typing)
  return (
    <ul className="conversation-list">
      {items.map((c) => {
        const title = conversationTitle(c)
        const online = c.peer ? (presence[c.peer.id]?.online ?? c.peer.online) : null
        const someoneTyping = Object.keys(typing[c.id] ?? {}).length > 0
        const body = c.lastMessage?.isDeleted ? t('message.deleted') : (c.lastMessage?.body ?? '')
        const preview = someoneTyping
          ? t('list.typing')
          : c.lastMessage
            ? `${c.lastMessage.senderId === me ? t('list.youPrefix') : c.type === 'Group' ? `${c.lastMessage.senderDisplayName}: ` : ''}${body}`
            : t('list.noMessages')
        return (
          <li key={c.id}>
            <NavLink to={`/chats/${c.id}`} className={({ isActive }) => `conversation-item${isActive ? ' active' : ''}`}>
              <Avatar name={title} online={online} />
              <span style={{ minWidth: 0 }}>
                <span className="conversation-title">
                  {c.type === 'Group' ? '# ' : ''}
                  {title}
                </span>
                <span className={`conversation-preview${someoneTyping ? ' typing-preview' : ''}`}>{preview}</span>
              </span>
              <span className="conversation-meta">
                <span>{formatListTime(c.lastMessageAt ?? c.createdAt)}</span>
                {c.unreadCount > 0 ? (
                  <span className="unread" aria-label={t('list.unread', { count: c.unreadCount })}>
                    {c.unreadCount}
                  </span>
                ) : null}
              </span>
            </NavLink>
          </li>
        )
      })}
    </ul>
  )
}

export function ChatsHome() {
  const t = useT()
  return (
    <div className="empty home">
      <div className="home-badge" aria-hidden="true">
        💬
      </div>
      <h2>{t('home.title')}</h2>
      <p>{t('home.hint')}</p>
    </div>
  )
}
