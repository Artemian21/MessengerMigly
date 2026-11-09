import { useCallback, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { conversationsApi } from '../api/endpoints'
import { mergeMessages, messagesKey, type MessagesCache } from '../lib/messages'

/**
 * MSG-03: newest page first, older pages on demand (cursor `before`). The cache is kept current by realtime events,
 * so it never goes stale on its own; reconnects resync it explicitly.
 */
export function useMessages(conversationId: string) {
  const queryClient = useQueryClient()
  const [loadingOlder, setLoadingOlder] = useState(false)
  const [prepended, setPrepended] = useState(0)

  const query = useQuery({
    queryKey: messagesKey(conversationId),
    queryFn: async (): Promise<MessagesCache> => {
      const page = await conversationsApi.messages(conversationId, { limit: 50 })
      return { items: mergeMessages([], page.items), hasOlder: page.hasMore }
    },
    staleTime: Infinity,
  })

  const loadOlder = useCallback(async () => {
    const cache = queryClient.getQueryData<MessagesCache>(messagesKey(conversationId))
    if (!cache?.hasOlder || loadingOlder) return
    const oldest = cache.items.find((m) => !m.pending)
    if (!oldest) return
    setLoadingOlder(true)
    try {
      const page = await conversationsApi.messages(conversationId, { before: oldest.sequence, limit: 50 })
      queryClient.setQueryData<MessagesCache>(messagesKey(conversationId), (current) =>
        current ? { items: mergeMessages(current.items, page.items), hasOlder: page.hasMore } : current,
      )
      setPrepended((count) => count + page.items.length)
    } finally {
      setLoadingOlder(false)
    }
  }, [conversationId, loadingOlder, queryClient])

  return { ...query, loadOlder, loadingOlder, prepended }
}
