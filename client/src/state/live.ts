import { create } from 'zustand'
import type { Presence } from '../api/types'

export const TYPING_TTL_MS = 5000

interface LiveState {
  presence: Record<string, Presence>
  /** conversationId → userId → expiry (epoch ms). Typing is ephemeral and stops by itself. */
  typing: Record<string, Record<string, number>>
  setPresence: (items: Presence[]) => void
  typingStarted: (conversationId: string, userId: string) => void
  typingStopped: (conversationId: string, userId: string) => void
  pruneTyping: () => void
}

export const useLive = create<LiveState>((set, get) => ({
  presence: {},
  typing: {},
  setPresence: (items) =>
    set((state) => {
      const presence = { ...state.presence }
      for (const item of items) presence[item.userId] = item
      return { presence }
    }),
  typingStarted: (conversationId, userId) =>
    set((state) => ({
      typing: {
        ...state.typing,
        [conversationId]: { ...state.typing[conversationId], [userId]: Date.now() + TYPING_TTL_MS },
      },
    })),
  typingStopped: (conversationId, userId) =>
    set((state) => {
      const current = { ...state.typing[conversationId] }
      delete current[userId]
      return { typing: { ...state.typing, [conversationId]: current } }
    }),
  pruneTyping: () => {
    const now = Date.now()
    const typing = get().typing
    let changed = false
    const next: LiveState['typing'] = {}
    for (const [conversationId, users] of Object.entries(typing)) {
      next[conversationId] = {}
      for (const [userId, expires] of Object.entries(users)) {
        if (expires > now) next[conversationId][userId] = expires
        else changed = true
      }
    }
    if (changed) set({ typing: next })
  },
}))
