import { memo } from 'react'
import type { UiMessage } from '../api/types'
import { formatBytes, formatTime } from '../lib/format'
import { canEdit } from '../lib/messages'
import { useT } from '../i18n'

export interface MessageActions {
  onReply: (message: UiMessage) => void
  onEdit: (message: UiMessage) => void
  onDelete: (message: UiMessage) => void
  onInfo: (message: UiMessage) => void
  onHistory: (message: UiMessage) => void
  onRetry: (message: UiMessage) => void
  onJumpTo: (messageId: string) => void
}

interface Props extends MessageActions {
  message: UiMessage
  me: string
  showAuthor: boolean
  highlighted?: boolean
}

/** Message bodies are always rendered as plain text, never as HTML (NFR-03). */
export const MessageBubble = memo(function MessageBubble({ message, me, showAuthor, highlighted, ...actions }: Props) {
  const t = useT()
  const mine = message.sender.id === me
  const classes = ['bubble', message.isDeleted ? 'deleted' : '', message.pending === 'failed' ? 'failed' : ''].filter(Boolean).join(' ')
  const reply = message.replyTo
  return (
    <div className={`message${mine ? ' mine' : ''}${highlighted ? ' highlight' : ''}`} data-message-id={message.id}>
      <div className={classes}>
        {showAuthor && !mine ? (
          <div className={`bubble-author${message.sender.isDeleted ? ' deleted-user' : ''}`}>{message.sender.displayName}</div>
        ) : null}
        {reply ? (
          <button type="button" className="reply-quote" onClick={() => actions.onJumpTo(reply.id)}>
            <strong>{reply.senderDisplayName}</strong>: {reply.isDeleted ? t('message.deleted') : (reply.bodyPreview ?? t('message.beforeJoin'))}
          </button>
        ) : null}
        <div className="bubble-text">{message.isDeleted ? t('message.deleted') : message.body}</div>
        {message.attachments.map((a) => (
          <div key={a.id} className="attachment" title={t('message.attachmentHint')}>
            📎 {a.fileName} · {formatBytes(a.sizeBytes)}
          </div>
        ))}
        <div className="bubble-footer">
          {message.isEdited && !message.isDeleted ? <span>{t('message.edited')}</span> : null}
          <time dateTime={message.createdAt}>{formatTime(message.createdAt)}</time>
          {mine ? <StatusTicks message={message} /> : null}
        </div>
        {!message.isDeleted && !message.pending ? (
          <div className="message-actions" role="toolbar" aria-label={t('message.actions')}>
            <button type="button" onClick={() => actions.onReply(message)}>
              {t('message.reply')}
            </button>
            {canEdit(message, me) ? (
              <button type="button" onClick={() => actions.onEdit(message)}>
                {t('message.edit')}
              </button>
            ) : null}
            {mine ? (
              <>
                <button type="button" onClick={() => actions.onInfo(message)}>
                  {t('message.info')}
                </button>
                {message.isEdited ? (
                  <button type="button" onClick={() => actions.onHistory(message)}>
                    {t('message.history')}
                  </button>
                ) : null}
                <button type="button" onClick={() => actions.onDelete(message)}>
                  {t('message.delete')}
                </button>
              </>
            ) : null}
          </div>
        ) : null}
        {message.pending === 'failed' ? (
          <button type="button" className="btn small danger" style={{ marginTop: 6 }} onClick={() => actions.onRetry(message)}>
            {t('message.retry')}
          </button>
        ) : null}
      </div>
    </div>
  )
})

function StatusTicks({ message }: { message: UiMessage }) {
  const t = useT()
  if (message.pending === 'sending') return <span aria-label={t('status.sending')}>◷</span>
  if (message.pending === 'failed') return <span aria-label={t('status.failed')}>!</span>
  if (message.isDeleted || !message.status) return null
  const label = t(`status.${message.status}`)
  return (
    <span className={`ticks${message.status === 'Read' ? ' read' : ''}`} aria-label={label} title={label}>
      {message.status === 'Sent' ? '✓' : '✓✓'}
    </span>
  )
}
