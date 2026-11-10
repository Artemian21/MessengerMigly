import type { Message } from '../api/types'

let sequence = 0

export function message(overrides: Partial<Message> = {}): Message {
  sequence++
  return {
    id: `m-${sequence}`,
    conversationId: 'c-1',
    sequence,
    updatedSequence: sequence,
    sender: { id: 'alice', username: 'alice', displayName: 'Alice', isDeleted: false },
    body: `message ${sequence}`,
    isDeleted: false,
    isEdited: false,
    replyTo: null,
    clientMessageId: `client-${sequence}`,
    createdAt: '2026-10-01T12:00:00Z',
    editedAt: null,
    deletedAt: null,
    attachments: [],
    status: null,
    ...overrides,
  }
}
