import { describe, expect, it } from 'vitest'
import { renderHook } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { useUnreadAtOpen } from './useUnreadAtOpen'
import { conversationsKey } from '../lib/messages'
import type { Conversation } from '../api/types'

const conversation = (patch: Partial<Conversation>): Conversation => ({
  id: 'c1',
  type: 'Direct',
  title: null,
  peer: null,
  myRole: 'Member',
  memberCount: 2,
  lastSequence: 8,
  lastReadSequence: 3,
  visibleFromSequence: 1,
  unreadCount: 5,
  lastMessage: null,
  lastMessageAt: null,
  createdAt: '2026-11-10T12:00:00Z',
  isDeleted: false,
  ...patch,
})

function setup(list: Conversation[] | undefined, initialFresh: Conversation | undefined) {
  const client = new QueryClient()
  if (list) client.setQueryData(conversationsKey, list)
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>
  return renderHook(({ fresh }) => useUnreadAtOpen('c1', fresh), { wrapper, initialProps: { fresh: initialFresh } })
}

describe('useUnreadAtOpen', () => {
  it('takes what the conversation list says when the chat is opened', () => {
    const { result } = setup([conversation({ unreadCount: 5, lastReadSequence: 3 })], undefined)

    expect(result.current).toEqual({ count: 5, after: 3 })
  })

  it('does not offer a summary when returning to a chat that was read, even if the conversation query still holds old numbers', () => {
    // the list was updated by "conversation read"; the per-conversation cache is stale (5 unread) and not fetched this visit
    const { result } = setup([conversation({ unreadCount: 0, lastReadSequence: 8 })], undefined)

    expect(result.current).toEqual({ count: 0, after: 8 })
  })

  it('ignores a stale conversation query when the list knows better, even once a fetch arrives', () => {
    const { result, rerender } = setup([conversation({ unreadCount: 0, lastReadSequence: 8 })], undefined)

    rerender({ fresh: conversation({ unreadCount: 5, lastReadSequence: 3 }) })

    expect(result.current).toEqual({ count: 0, after: 8 })
  })

  it('falls back to the conversation fetched during this visit when the list is not loaded (page opened directly)', () => {
    const { result, rerender } = setup(undefined, undefined)
    expect(result.current).toBeNull()

    rerender({ fresh: conversation({ unreadCount: 4, lastReadSequence: 2 }) })

    expect(result.current).toEqual({ count: 4, after: 2 })
  })

  it('keeps the first snapshot for the whole visit', () => {
    const { result, rerender } = setup(undefined, conversation({ unreadCount: 4, lastReadSequence: 2 }))

    rerender({ fresh: conversation({ unreadCount: 0, lastReadSequence: 9 }) })

    expect(result.current).toEqual({ count: 4, after: 2 })
  })

  it('does not take the list entry of another conversation', () => {
    const { result } = setup([conversation({ id: 'other', unreadCount: 7 })], undefined)

    expect(result.current).toBeNull()
  })
})
