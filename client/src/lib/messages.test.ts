import { describe, expect, it } from 'vitest'
import { applyDeletion, applyStatus, canEdit, DELETED_PLACEHOLDER, markFailed, maxSequence, mergeMessages, optimisticMessage } from './messages'
import { message } from '../test/factories'

const me = { id: 'alice', username: 'alice', displayName: 'Alice' }

describe('mergeMessages', () => {
  it('orders by server sequence regardless of arrival order', () => {
    const a = message({ sequence: 3, updatedSequence: 3 })
    const b = message({ sequence: 1, updatedSequence: 1 })
    const c = message({ sequence: 2, updatedSequence: 2 })
    expect(mergeMessages([], [a, b, c]).map((m) => m.sequence)).toEqual([1, 2, 3])
  })

  it('replaces the optimistic copy with the confirmed message by clientMessageId', () => {
    const pending = optimisticMessage('c-1', 'client-x', 'hello', me, null)
    const confirmed = message({ clientMessageId: 'client-x', body: 'hello', sequence: 7, updatedSequence: 7 })
    const merged = mergeMessages([pending], [confirmed])
    expect(merged).toHaveLength(1)
    expect(merged[0].id).toBe(confirmed.id)
    expect(merged[0].pending).toBeUndefined()
  })

  it('keeps pending messages after confirmed ones', () => {
    const pending = optimisticMessage('c-1', 'client-y', 'later', me, null)
    const merged = mergeMessages([pending], [message({ sequence: 10, updatedSequence: 10 })])
    expect(merged.at(-1)?.pending).toBe('sending')
  })

  it('ignores stale revisions and applies newer edits (idempotent resync)', () => {
    const original = message({ sequence: 5, updatedSequence: 5, body: 'v1' })
    const edited = { ...original, updatedSequence: 9, body: 'v2', isEdited: true }
    let items = mergeMessages([], [original])
    items = mergeMessages(items, [edited])
    items = mergeMessages(items, [original])
    expect(items).toHaveLength(1)
    expect(items[0].body).toBe('v2')
  })

  it('never downgrades a status', () => {
    const read = message({ status: 'Read' })
    const merged = mergeMessages([read], [{ ...read, status: 'Delivered' }])
    expect(merged[0].status).toBe('Read')
  })
})

describe('message state helpers', () => {
  it('applies status upgrades only', () => {
    const m = message({ status: 'Delivered' })
    expect(applyStatus([m], m.id, 'Read')[0].status).toBe('Read')
    expect(applyStatus([{ ...m, status: 'Read' }], m.id, 'Delivered')[0].status).toBe('Read')
  })

  it('turns deletions into tombstones and updates reply previews', () => {
    const original = message()
    const reply = message({ replyTo: { id: original.id, sequence: original.sequence, senderDisplayName: 'Alice', bodyPreview: 'secret', isDeleted: false } })
    const [tomb, survivingReply] = applyDeletion([original, reply], original.id, 99)
    expect(tomb.isDeleted).toBe(true)
    expect(tomb.body).toBe(DELETED_PLACEHOLDER)
    expect(survivingReply.replyTo?.bodyPreview).toBe(DELETED_PLACEHOLDER)
  })

  it('marks failed sends for retry', () => {
    const pending = optimisticMessage('c-1', 'client-z', 'oops', me, null)
    expect(markFailed([pending], 'client-z')[0].pending).toBe('failed')
  })

  it('computes the highest confirmed sequence', () => {
    const items = [message({ sequence: 4 }), optimisticMessage('c-1', 'p', 'x', me, null)]
    expect(maxSequence(items)).toBe(4)
  })

  it('allows editing own messages within 48 hours only', () => {
    const created = Date.parse('2026-10-01T12:00:00Z')
    const own = message({ createdAt: '2026-10-01T12:00:00Z' })
    expect(canEdit(own, 'alice', created + 47 * 3600_000)).toBe(true)
    expect(canEdit(own, 'alice', created + 49 * 3600_000)).toBe(false)
    expect(canEdit(own, 'bob', created)).toBe(false)
  })
})
