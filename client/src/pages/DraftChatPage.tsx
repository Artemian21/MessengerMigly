import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom'
import { conversationsApi, usersApi } from '../api/endpoints'
import { ApiError } from '../api/http'
import { conversationsKey, newClientMessageId } from '../lib/messages'
import { formatLastSeen } from '../lib/format'
import { Composer } from '../components/Composer'
import { Avatar, ErrorNotice } from '../components/ui'
import { useT } from '../i18n'

/** CNV-02: a personal chat exists only after the first message; until then this is a local draft. */
export function DraftChatPage() {
  const t = useT()
  const { peerId } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [error, setError] = useState<unknown>(null)
  const [sending, setSending] = useState(false)
  const existing = useQuery({
    queryKey: ['direct', peerId],
    queryFn: () => conversationsApi.direct(peerId!).catch((e) => (e instanceof ApiError && e.status === 404 ? null : Promise.reject(e))),
  })
  const peer = useQuery({ queryKey: ['user', peerId], queryFn: () => usersApi.get(peerId!) })

  if (existing.data) return <Navigate to={`/chats/${existing.data.id}`} replace />

  const send = async (body: string) => {
    setSending(true)
    setError(null)
    try {
      const message = await conversationsApi.sendDirect(peerId!, { clientMessageId: newClientMessageId(), body })
      await queryClient.invalidateQueries({ queryKey: conversationsKey })
      navigate(`/chats/${message.conversationId}`, { replace: true })
    } catch (e) {
      setError(e)
      setSending(false)
    }
  }

  return (
    <>
      <header className="chat-header">
        <Link to="/" className="btn small ghost back-link icon" aria-label={t('common.backToChats')}>
          ←
        </Link>
        {peer.data ? <Avatar name={peer.data.displayName} /> : null}
        <div>
          <h2>{peer.data?.displayName ?? '…'}</h2>
          {peer.data ? (
            <div className="chat-subtitle">
              @{peer.data.username} · {formatLastSeen(peer.data.online, peer.data.lastSeenAt)}
            </div>
          ) : null}
        </div>
      </header>
      <div className="chat-body">
        <p className="empty">{existing.isLoading ? t('chat.draftChecking') : t('chat.draftHint')}</p>
      </div>
      <div style={{ padding: '0 16px' }}>
        <ErrorNotice error={error ?? peer.error} />
      </div>
      <Composer disabled={sending || existing.isLoading} disabledReason={t('chat.sending')} replyTo={null} editing={null} onCancelContext={() => undefined} onSend={send} onEdit={() => undefined} />
    </>
  )
}
