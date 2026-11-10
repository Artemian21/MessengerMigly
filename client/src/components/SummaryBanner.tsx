import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { aiApi } from '../api/endpoints'
import { useSummary } from '../hooks/useSummary'
import { toSummaryErrorCode } from '../lib/summary'
import { useT } from '../i18n'

/** Unread state when the chat was opened. Opening a chat marks it read, so the banner has to remember it. */
export interface UnreadSnapshot {
  count: number
  /** The read position at that moment: the summary covers everything after it. */
  after: number
}

/** "What did I miss": offers a summary of the unread messages, shows its progress, result or error. */
export function SummaryBanner({ conversationId, unread }: { conversationId: string; unread: UnreadSnapshot | null }) {
  const t = useT()
  const config = useQuery({ queryKey: ['ai-config'], queryFn: aiApi.config, staleTime: Infinity, retry: false })
  const { view, start, dismiss } = useSummary(conversationId)
  const [hidden, setHidden] = useState(false)

  if (!config.data?.summaryEnabled) return null
  const offer = !view && !hidden && unread !== null && unread.count >= config.data.minMessages

  if (offer) {
    return (
      <section className="summary" aria-label={t('summary.title')}>
        <div className="summary-row">
          <div>
            <strong>{t('summary.banner', { count: unread.count })}</strong>
            <div className="faint summary-note">{t('summary.privacy')}</div>
          </div>
          <span className="spacer" />
          <button className="btn small primary" onClick={() => void start(unread.after)}>
            {t('summary.action')}
          </button>
          <button className="btn small ghost" onClick={() => setHidden(true)}>
            {t('summary.hide')}
          </button>
        </div>
      </section>
    )
  }

  if (!view) return null

  return (
    <section className="summary" aria-label={t('summary.title')}>
      {view.status === 'pending' ? (
        <p className="summary-text" role="status" aria-busy="true">
          {t('summary.pending')}
        </p>
      ) : null}

      {view.status === 'ready' ? (
        <>
          <div className="summary-row">
            <h3 className="summary-title">{t('summary.title')}</h3>
            <span className="spacer" />
            <button className="btn small ghost icon" onClick={dismiss} aria-label={t('common.close')}>
              ✕
            </button>
          </div>
          {/* Plain text only: the summary is model output built from other people's messages. */}
          <p className="summary-text">{view.summary}</p>
          <p className="faint summary-note">
            {view.truncated ? `${t('summary.truncated', { count: view.messageCount })} ` : ''}
            {t('summary.aiNote')}
          </p>
        </>
      ) : null}

      {view.status === 'failed' ? (
        <div className="summary-row">
          <p className="summary-text summary-error" role="alert">
            {t(`summary.error.${toSummaryErrorCode(view.errorCode)}`)}
          </p>
          <span className="spacer" />
          <button className="btn small" onClick={() => void start(unread?.after)}>
            {t('summary.retry')}
          </button>
          <button className="btn small ghost icon" onClick={dismiss} aria-label={t('common.close')}>
            ✕
          </button>
        </div>
      ) : null}

      {view.status === 'empty' ? (
        <div className="summary-row">
          <p className="summary-text">{t('summary.empty')}</p>
          <span className="spacer" />
          <button className="btn small ghost icon" onClick={dismiss} aria-label={t('common.close')}>
            ✕
          </button>
        </div>
      ) : null}
    </section>
  )
}
