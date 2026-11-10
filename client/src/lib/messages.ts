import type { Message, MessageStatus, UiMessage } from '../api/types'

export const DELETED_PLACEHOLDER = '[Message deleted]'

export interface MessagesCache {
  items: UiMessage[]
  hasOlder: boolean
}

export const messagesKey = (conversationId: string) => ['messages', conversationId] as const
export const conversationsKey = ['conversations'] as const

const statusRank: Record<MessageStatus, number> = { Sent: 0, Delivered: 1, Read: 2 }

function sortMessages(items: UiMessage[]): UiMessage[] {
  const confirmed = items.filter((m) => !m.pending).sort((a, b) => a.sequence - b.sequence)
  const pending = items.filter((m) => m.pending)
  return [...confirmed, ...pending]
}

/**
 * Merges server messages into the cache. A confirmed message replaces its optimistic copy (same clientMessageId),
 * and a newer revision (higher updatedSequence) replaces an older one, so edits and resyncs are idempotent.
 */
export function mergeMessages(items: UiMessage[], incoming: Message[]): UiMessage[] {
  const next = [...items]
  for (const message of incoming) {
    const index = next.findIndex(
      (m) => m.id === message.id || (m.pending !== undefined && m.clientMessageId === message.clientMessageId),
    )
    if (index < 0) {
      next.push(message)
      continue
    }
    const current = next[index]
    if (current.pending || message.updatedSequence >= current.updatedSequence) {
      next[index] = { ...message, status: higherStatus(current.status, message.status) }
    }
  }
  return sortMessages(next)
}

function higherStatus(a: MessageStatus | null, b: MessageStatus | null): MessageStatus | null {
  if (!a) return b
  if (!b) return a
  return statusRank[a] >= statusRank[b] ? a : b
}

export function applyStatus(items: UiMessage[], messageId: string, status: MessageStatus): UiMessage[] {
  return items.map((m) => (m.id === messageId ? { ...m, status: higherStatus(m.status, status) } : m))
}

export function applyDeletion(items: UiMessage[], messageId: string, updatedSequence: number): UiMessage[] {
  return items.map((m) => {
    if (m.id === messageId) {
      return { ...m, isDeleted: true, body: DELETED_PLACEHOLDER, attachments: [], updatedSequence, deletedAt: new Date().toISOString() }
    }
    if (m.replyTo?.id === messageId) {
      return { ...m, replyTo: { ...m.replyTo, isDeleted: true, bodyPreview: DELETED_PLACEHOLDER } }
    }
    return m
  })
}

export function markFailed(items: UiMessage[], clientMessageId: string): UiMessage[] {
  return items.map((m) => (m.clientMessageId === clientMessageId && m.pending ? { ...m, pending: 'failed' } : m))
}

export function maxSequence(items: UiMessage[]): number {
  return items.reduce((max, m) => (m.pending ? max : Math.max(max, m.sequence)), 0)
}

export function maxUpdatedSequence(items: UiMessage[]): number {
  return items.reduce((max, m) => (m.pending ? max : Math.max(max, m.updatedSequence)), 0)
}

export function newClientMessageId(): string {
  return crypto.randomUUID()
}

export function optimisticMessage(
  conversationId: string,
  clientMessageId: string,
  body: string,
  sender: { id: string; username: string; displayName: string },
  replyTo: UiMessage | null,
): UiMessage {
  return {
    id: `pending-${clientMessageId}`,
    conversationId,
    sequence: Number.MAX_SAFE_INTEGER,
    updatedSequence: 0,
    sender: { ...sender, isDeleted: false },
    body,
    isDeleted: false,
    isEdited: false,
    replyTo: replyTo
      ? {
          id: replyTo.id,
          sequence: replyTo.sequence,
          senderDisplayName: replyTo.sender.displayName,
          bodyPreview: replyTo.body,
          isDeleted: replyTo.isDeleted,
        }
      : null,
    clientMessageId,
    createdAt: new Date().toISOString(),
    editedAt: null,
    deletedAt: null,
    attachments: [],
    status: null,
    pending: 'sending',
  }
}

export const EDIT_WINDOW_MS = 48 * 60 * 60 * 1000

export function canEdit(message: UiMessage, userId: string, now = Date.now()): boolean {
  return message.sender.id === userId && !message.isDeleted && !message.pending && now - Date.parse(message.createdAt) <= EDIT_WINDOW_MS
}
