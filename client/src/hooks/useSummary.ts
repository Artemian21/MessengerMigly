import { useCallback, useEffect } from 'react'
import { ApiError } from '../api/http'
import { conversationsApi } from '../api/endpoints'
import { useSummaries, viewFromJob } from '../state/summaries'
import { startErrorCode } from '../lib/summary'

/** The hub event normally delivers the result; polling is the safety net for a missed event or a dropped connection. */
export const SUMMARY_POLL_MS = 3000

export function useSummary(conversationId: string) {
  const view = useSummaries((s) => s.byConversation[conversationId])
  const pendingJobId = view?.status === 'pending' ? view.jobId : null

  const start = useCallback(
    async (afterSequence?: number) => {
      const store = useSummaries.getState()
      store.set(conversationId, { status: 'pending', jobId: '', messageCount: 0, truncated: false }) // instant feedback
      try {
        const job = await conversationsApi.startSummary(conversationId, afterSequence)
        const next = viewFromJob(job)
        if (next) store.set(conversationId, next)
        else store.set(conversationId, { status: 'failed', errorCode: 'internal' })
      } catch (error) {
        store.set(conversationId, { status: 'failed', errorCode: startErrorCode(error) })
      }
    },
    [conversationId],
  )

  useEffect(() => {
    if (!pendingJobId) return
    const timer = setInterval(async () => {
      try {
        const job = await conversationsApi.summary(conversationId, pendingJobId)
        useSummaries.getState().finished(conversationId, job)
      } catch (error) {
        // 404: the job expired or the server restarted while it was running; there is nothing left to wait for.
        if (error instanceof ApiError && error.status === 404) {
          useSummaries.getState().set(conversationId, { status: 'failed', errorCode: 'internal' })
        }
      }
    }, SUMMARY_POLL_MS)
    return () => clearInterval(timer)
  }, [conversationId, pendingJobId])

  const dismiss = useCallback(() => useSummaries.getState().clear(conversationId), [conversationId])

  return { view, start, dismiss }
}
