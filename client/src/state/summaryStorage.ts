import { useAuth } from './auth'
import { useSummaries, type SummaryView } from './summaries'

/**
 * Summaries survive a page reload in this tab (sessionStorage) until the user closes them. They are the text of a
 * conversation, so they are kept per user, validated on load, and wiped when the session ends.
 */
const PREFIX = 'migly.summaries.'
const VERSION = 1

const keyFor = (userId: string) => `${PREFIX}${userId}`

function isView(value: unknown): value is SummaryView {
  if (typeof value !== 'object' || value === null) return false
  const v = value as Record<string, unknown>
  switch (v.status) {
    // A request that has no job id yet is not worth restoring: there would be nothing to poll.
    case 'pending':
      return typeof v.jobId === 'string' && v.jobId !== '' && typeof v.messageCount === 'number' && typeof v.truncated === 'boolean'
    case 'ready':
      return (
        typeof v.jobId === 'string' &&
        typeof v.summary === 'string' &&
        typeof v.messageCount === 'number' &&
        typeof v.truncated === 'boolean' &&
        typeof v.at === 'string'
      )
    case 'failed':
      return typeof v.errorCode === 'string'
    case 'empty':
      return true
    default:
      return false
  }
}

export function loadSummaries(userId: string): Record<string, SummaryView> {
  try {
    const raw = sessionStorage.getItem(keyFor(userId))
    if (!raw) return {}
    const parsed = JSON.parse(raw) as { v?: unknown; byConversation?: unknown }
    if (parsed.v !== VERSION || typeof parsed.byConversation !== 'object' || parsed.byConversation === null) return {}
    const result: Record<string, SummaryView> = {}
    for (const [conversationId, view] of Object.entries(parsed.byConversation)) {
      if (isView(view)) result[conversationId] = view
    }
    return result
  } catch {
    return {} // corrupt or unavailable storage: start empty
  }
}

export function saveSummaries(userId: string, byConversation: Record<string, SummaryView | undefined>) {
  try {
    const keep: Record<string, SummaryView> = {}
    for (const [conversationId, view] of Object.entries(byConversation)) {
      if (isView(view)) keep[conversationId] = view
    }
    if (Object.keys(keep).length === 0) sessionStorage.removeItem(keyFor(userId))
    else sessionStorage.setItem(keyFor(userId), JSON.stringify({ v: VERSION, byConversation: keep }))
  } catch {
    // Storage unavailable or full: the summary then lives only as long as the page.
  }
}

export function clearAllSummaries() {
  try {
    for (const key of Object.keys(sessionStorage)) {
      if (key.startsWith(PREFIX)) sessionStorage.removeItem(key)
    }
  } catch {
    // nothing to clear
  }
}

/**
 * Connects the summary store to sessionStorage: restores a user's summaries when they sign in (including the bootstrap after
 * a reload), saves every change, and wipes everything when the session ends (sign-out, expired session, deleted account).
 */
export function initSummaryPersistence(): () => void {
  let userId: string | null = null

  const onAuth = (state: ReturnType<typeof useAuth.getState>) => {
    if (state.status === 'anonymous') {
      userId = null // before the reset below, so it is not written back
      clearAllSummaries()
      useSummaries.setState({ byConversation: {} })
      return
    }
    const next = state.user?.id ?? null
    if (next !== userId) {
      userId = next
      useSummaries.setState({ byConversation: next ? loadSummaries(next) : {} })
    }
  }

  onAuth(useAuth.getState())
  const stopAuth = useAuth.subscribe(onAuth)
  const stopSummaries = useSummaries.subscribe((state) => {
    if (userId) saveSummaries(userId, state.byConversation)
  })
  return () => {
    stopAuth()
    stopSummaries()
  }
}
