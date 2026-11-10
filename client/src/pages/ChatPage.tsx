import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { Virtuoso, type VirtuosoHandle } from 'react-virtuoso'
import { conversationsApi, messagesApi } from '../api/endpoints'
import type { Conversation, Message, UiMessage } from '../api/types'
import { useAuth } from '../state/auth'
import { useLive } from '../state/live'
import { realtime } from '../realtime/hub'
import { useMessages } from '../hooks/useMessages'
import {
  applyDeletion,
  conversationsKey,
  markFailed,
  maxSequence,
  mergeMessages,
  messagesKey,
  newClientMessageId,
  optimisticMessage,
  type MessagesCache,
} from '../lib/messages'
import { conversationTitle } from '../lib/conversations'
import { formatDay, formatLastSeen, formatTime } from '../lib/format'
import { MessageBubble, type MessageActions } from '../components/MessageBubble'
import { Composer } from '../components/Composer'
import { GroupPanel } from '../components/GroupPanel'
import { SummaryBanner } from '../components/SummaryBanner'
import { useUnreadAtOpen } from '../hooks/useUnreadAtOpen'
import { useSummaries } from '../state/summaries'
import { Avatar, ErrorNotice, Modal } from '../components/ui'
import { useT } from '../i18n'

const FIRST_INDEX = 1_000_000

export function ChatPage() {
  const { conversationId } = useParams()
  return <ChatView key={conversationId} conversationId={conversationId!} />
}

function ChatView({ conversationId }: { conversationId: string }) {
  const t = useT()
  const me = useAuth((s) => s.user)!
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const conversation = useQuery({
    queryKey: ['conversation', conversationId],
    queryFn: () => conversationsApi.get(conversationId),
    placeholderData: () => queryClient.getQueryData<Conversation[]>(conversationsKey)?.find((c) => c.id === conversationId),
  })
  const messages = useMessages(conversationId)
  const items = useMemo(() => messages.data?.items ?? [], [messages.data])
  const [replyTo, setReplyTo] = useState<UiMessage | null>(null)
  const [editing, setEditing] = useState<UiMessage | null>(null)
  const [infoFor, setInfoFor] = useState<UiMessage | null>(null)
  const [historyFor, setHistoryFor] = useState<UiMessage | null>(null)
  const [panelOpen, setPanelOpen] = useState(false)
  const [highlight, setHighlight] = useState<string | null>(null)
  const [actionError, setActionError] = useState<unknown>(null)
  // Opening the chat marks it read, so what was unread at that moment is remembered for "summarize what I missed".
  const unreadAtOpen = useUnreadAtOpen(conversationId, conversation.isFetchedAfterMount ? conversation.data : undefined)
  const virtuoso = useRef<VirtuosoHandle>(null)
  const lastMarked = useRef(0)
  const acked = useRef(false)

  useEffect(() => {
    void realtime.openConversation(conversationId)
    return () => {
      void realtime.closeConversation(conversationId)
      useSummaries.getState().clearSettled(conversationId) // a summary belongs to this visit; a running one is kept
    }
  }, [conversationId])

  // STS-02: acknowledge what this device received on first load.
  useEffect(() => {
    if (acked.current || !messages.data) return
    acked.current = true
    realtime.ack(messages.data.items.filter((m) => m.sender.id !== me.id && !m.isDeleted).map((m) => m.id))
  }, [messages.data, me.id])

  // STS-03: mark read while the chat is open and the tab is visible.
  const markRead = useCallback(() => {
    const top = maxSequence(items)
    const already = Math.max(lastMarked.current, conversation.data?.lastReadSequence ?? 0)
    if (top > already && document.visibilityState === 'visible') {
      lastMarked.current = top
      void realtime.markRead(conversationId, top)
    }
  }, [items, conversation.data?.lastReadSequence, conversationId])

  useEffect(() => {
    markRead()
    document.addEventListener('visibilitychange', markRead)
    return () => document.removeEventListener('visibilitychange', markRead)
  }, [markRead])

  const updateCache = useCallback(
    (update: (items: UiMessage[]) => UiMessage[]) =>
      queryClient.setQueryData<MessagesCache>(messagesKey(conversationId), (cache) => (cache ? { ...cache, items: update(cache.items) } : cache)),
    [conversationId, queryClient],
  )

  const deliver = useCallback(
    async (clientMessageId: string, body: string, reply: UiMessage | null) => {
      try {
        const saved = await conversationsApi.send(conversationId, { clientMessageId, body, replyToMessageId: reply?.id ?? null })
        updateCache((current) => mergeMessages(current, [saved as Message]))
      } catch (error) {
        updateCache((current) => markFailed(current, clientMessageId))
        setActionError(error)
      }
    },
    [conversationId, updateCache],
  )

  const send = (body: string) => {
    const clientMessageId = newClientMessageId()
    updateCache((current) => [...current, optimisticMessage(conversationId, clientMessageId, body, me, replyTo)])
    const reply = replyTo
    setReplyTo(null)
    setActionError(null)
    requestAnimationFrame(() => virtuoso.current?.scrollToIndex({ index: 'LAST', align: 'end', behavior: 'smooth' }))
    void deliver(clientMessageId, body, reply)
  }

  const actions: MessageActions = {
    onReply: (m) => {
      setEditing(null)
      setReplyTo(m)
    },
    onEdit: (m) => {
      setReplyTo(null)
      setEditing(m)
    },
    onDelete: async (m) => {
      if (!confirm(t('chat.confirmDeleteMessage'))) return
      try {
        await messagesApi.remove(m.id)
        updateCache((current) => applyDeletion(current, m.id, m.updatedSequence + 1))
      } catch (error) {
        setActionError(error)
      }
    },
    onInfo: setInfoFor,
    onHistory: setHistoryFor,
    onRetry: (m) => {
      updateCache((current) => current.map((x) => (x.clientMessageId === m.clientMessageId ? { ...x, pending: 'sending' } : x)))
      // Same clientMessageId: the server returns the stored message if the first attempt actually landed.
      const reply = m.replyTo ? (items.find((x) => x.id === m.replyTo!.id) ?? null) : null
      void deliver(m.clientMessageId, m.body ?? '', reply)
    },
    onJumpTo: (id) => {
      const index = items.findIndex((x) => x.id === id)
      if (index >= 0) {
        virtuoso.current?.scrollToIndex({ index, align: 'center', behavior: 'smooth' })
        setHighlight(id)
        setTimeout(() => setHighlight(null), 1500)
      }
    },
  }

  const edit = async (message: UiMessage, body: string) => {
    setEditing(null)
    try {
      const saved = await messagesApi.edit(message.id, body)
      updateCache((current) => mergeMessages(current, [saved]))
    } catch (error) {
      setActionError(error)
    }
  }

  const clearChat = async () => {
    if (!confirm(t('chat.confirmDeleteChat'))) return
    await conversationsApi.clear(conversationId)
    await queryClient.invalidateQueries({ queryKey: conversationsKey })
    queryClient.removeQueries({ queryKey: messagesKey(conversationId) })
    navigate('/')
  }

  const c = conversation.data
  const title = c ? conversationTitle(c) : '…'
  const peerDeleted = c?.peer?.isDeleted ?? false

  if (conversation.isError || messages.isError) {
    return (
      <div className="page">
        <ErrorNotice error={conversation.error ?? messages.error} />
        <Link to="/">{t('common.backToChats')}</Link>
      </div>
    )
  }

  return (
    <>
      <header className="chat-header">
        <Link to="/" className="btn small ghost back-link icon" aria-label={t('common.backToChats')}>
          ←
        </Link>
        <Avatar name={title} />
        <div style={{ minWidth: 0 }}>
          <h2>
            {c?.type === 'Group' ? '# ' : ''}
            {title}
          </h2>
          <ChatSubtitle conversation={c} />
        </div>
        <span className="spacer" />
        {c?.type === 'Group' ? (
          <button className="btn small" onClick={() => setPanelOpen((v) => !v)} aria-expanded={panelOpen}>
            {t('chat.members')}
          </button>
        ) : (
          <button className="btn small ghost" onClick={clearChat}>
            {t('chat.deleteChat')}
          </button>
        )}
      </header>
      <SummaryBanner conversationId={conversationId} unread={unreadAtOpen} />
      <div className="chat-body" style={{ position: 'relative' }}>
        <div className="chat-messages" aria-live="polite" aria-relevant="additions">
          {messages.isLoading ? <p className="empty">{t('chat.loading')}</p> : null}
          {messages.data && items.length === 0 ? <p className="empty">{t('chat.empty')}</p> : null}
          {items.length > 0 ? (
            <Virtuoso
              ref={virtuoso}
              style={{ height: '100%' }}
              data={items}
              firstItemIndex={FIRST_INDEX - messages.prepended}
              initialTopMostItemIndex={items.length - 1}
              startReached={() => void messages.loadOlder()}
              followOutput={(atBottom) => (atBottom ? 'smooth' : false)}
              computeItemKey={(_, m) => m.clientMessageId}
              components={{
                Header: () =>
                  messages.data?.hasOlder ? (
                    <p className="day-divider">{messages.loadingOlder ? t('chat.loadingOlder') : t('chat.scrollForOlder')}</p>
                  ) : (
                    <p className="day-divider">{t('chat.beginning')}</p>
                  ),
              }}
              itemContent={(index, message) => {
                const local = index - (FIRST_INDEX - messages.prepended)
                const previous = local > 0 ? items[local - 1] : undefined
                const newDay = !previous || formatDay(previous.createdAt) !== formatDay(message.createdAt)
                return (
                  <>
                    {newDay ? <div className="day-divider">{formatDay(message.createdAt)}</div> : null}
                    <MessageBubble
                      message={message}
                      me={me.id}
                      showAuthor={c?.type === 'Group' && (newDay || previous?.sender.id !== message.sender.id)}
                      highlighted={highlight === message.id}
                      {...actions}
                    />
                  </>
                )
              }}
            />
          ) : null}
        </div>
        {panelOpen && c ? <GroupPanel conversation={c} onClose={() => setPanelOpen(false)} /> : null}
      </div>
      <TypingLine conversationId={conversationId} conversation={c} />
      {actionError ? (
        <div style={{ padding: '0 16px' }}>
          <ErrorNotice error={actionError} />
        </div>
      ) : null}
      <Composer
        key={editing?.id ?? 'compose'}
        disabled={c?.isDeleted || peerDeleted}
        disabledReason={peerDeleted ? t('chat.peerDeleted') : t('chat.groupDeleted')}
        replyTo={replyTo}
        editing={editing}
        onCancelContext={() => {
          setReplyTo(null)
          setEditing(null)
        }}
        onSend={send}
        onEdit={edit}
        onTyping={() => realtime.typing(conversationId)}
      />
      {infoFor ? <MessageInfoModal message={infoFor} onClose={() => setInfoFor(null)} /> : null}
      {historyFor ? <HistoryModal message={historyFor} onClose={() => setHistoryFor(null)} /> : null}
    </>
  )
}

function ChatSubtitle({ conversation }: { conversation: Conversation | undefined }) {
  const t = useT()
  const presence = useLive((s) => (conversation?.peer ? s.presence[conversation.peer.id] : undefined))
  if (!conversation) return null
  if (conversation.type === 'Group') {
    return <div className="chat-subtitle">{conversation.isDeleted ? t('chat.deletedGroup') : t('chat.memberCount', { count: conversation.memberCount })}</div>
  }
  const peer = conversation.peer
  if (!peer) return null
  if (peer.isDeleted) return <div className="chat-subtitle">{t('chat.deletedAccount')}</div>
  return (
    <div className="chat-subtitle">
      @{peer.username} · {formatLastSeen(presence?.online ?? peer.online, presence?.lastSeenAt ?? peer.lastSeenAt)}
    </div>
  )
}

function TypingLine({ conversationId, conversation }: { conversationId: string; conversation: Conversation | undefined }) {
  const t = useT()
  const typing = useLive((s) => s.typing[conversationId])
  const prune = useLive((s) => s.pruneTyping)
  const members = useQuery({
    queryKey: ['members', conversationId],
    queryFn: () => conversationsApi.members(conversationId),
    enabled: conversation?.type === 'Group',
  })
  useEffect(() => {
    const timer = setInterval(prune, 1000)
    return () => clearInterval(timer)
  }, [prune])
  const ids = Object.keys(typing ?? {})
  const names = ids.map((id) =>
    conversation?.peer?.id === id ? conversation.peer.displayName : (members.data?.find((m) => m.userId === id)?.displayName ?? t('chat.someone')),
  )
  return (
    <div className="typing" aria-live="polite" style={{ visibility: names.length ? 'visible' : 'hidden' }}>
      {names.length ? t(names.length > 1 ? 'chat.areTyping' : 'chat.isTyping', { names: names.join(', ') }) : '·'}
    </div>
  )
}

function MessageInfoModal({ message, onClose }: { message: UiMessage; onClose: () => void }) {
  const t = useT()
  const info = useQuery({ queryKey: ['message-info', message.id], queryFn: () => messagesApi.info(message.id) })
  return (
    <Modal title={t('info.title')} onClose={onClose}>
      <p className="muted">{(message.body ?? '').slice(0, 120)}</p>
      <ErrorNotice error={info.error} />
      {info.data ? (
        <>
          <p>
            {t('info.status')} <strong>{t(`status.${info.data.status}`)}</strong>
          </p>
          <table className="table">
            <thead>
              <tr>
                <th>{t('info.recipient')}</th>
                <th>{t('info.delivered')}</th>
                <th>{t('info.read')}</th>
              </tr>
            </thead>
            <tbody>
              {info.data.recipients.map((r) => (
                <tr key={r.userId}>
                  <td>{r.displayName}</td>
                  <td>{r.deliveredAt ? `${formatDay(r.deliveredAt)} ${formatTime(r.deliveredAt)}` : '—'}</td>
                  <td>{r.readAt ? `${formatDay(r.readAt)} ${formatTime(r.readAt)}` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      ) : null}
    </Modal>
  )
}

function HistoryModal({ message, onClose }: { message: UiMessage; onClose: () => void }) {
  const t = useT()
  const history = useQuery({ queryKey: ['message-history', message.id], queryFn: () => messagesApi.history(message.id) })
  return (
    <Modal title={t('history.title')} onClose={onClose}>
      <ErrorNotice error={history.error} />
      <ul className="list">
        {(history.data ?? []).map((h, i) => (
          <li key={i}>
            <span className="faint">{`${formatDay(h.editedAt)} ${formatTime(h.editedAt)}`}</span>
            <span className="bubble-text">{h.previousBody}</span>
          </li>
        ))}
        <li>
          <span className="faint">{t('history.now')}</span>
          <span className="bubble-text">{message.body}</span>
        </li>
      </ul>
    </Modal>
  )
}
