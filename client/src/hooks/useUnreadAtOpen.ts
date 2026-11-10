import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import type { Conversation } from '../api/types'
import type { UnreadSnapshot } from '../components/SummaryBanner'
import { conversationsKey } from '../lib/messages'

const snapshotOf = (c: Conversation | undefined): UnreadSnapshot | null =>
  c ? { count: c.unreadCount, after: c.lastReadSequence } : null

/**
 * What was unread at the moment the chat was opened ("summarize what I missed" works from it, because opening a chat
 * marks it read). Taken once per visit.
 *
 * The conversation list is the source: realtime events keep it current, and it drops to 0 once the chat is read. The
 * per-conversation query is NOT: its cache outlives a visit and is only refreshed by refetching, so right after returning
 * to a chat it still says "5 unread". It is used only when it was fetched during this visit (`fresh`), for the case where
 * the page was opened directly and the list is not loaded yet.
 */
export function useUnreadAtOpen(conversationId: string, fresh: Conversation | undefined): UnreadSnapshot | null {
  const queryClient = useQueryClient()
  const [snapshot, setSnapshot] = useState<UnreadSnapshot | null>(() =>
    snapshotOf(queryClient.getQueryData<Conversation[]>(conversationsKey)?.find((c) => c.id === conversationId)),
  )
  if (snapshot === null && fresh) setSnapshot(snapshotOf(fresh))
  return snapshot
}
