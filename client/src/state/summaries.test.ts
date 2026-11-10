import { beforeEach, describe, expect, it } from 'vitest'
import { useSummaries, viewFromJob } from './summaries'
import { startErrorCode, toSummaryErrorCode } from '../lib/summary'
import { ApiError } from '../api/http'

const pending = { status: 'pending', jobId: 'job-1', messageCount: 12, truncated: true } as const

describe('summaries store', () => {
  beforeEach(() => useSummaries.setState({ byConversation: {} }))

  it('turns a finished job into the view, keeping the counts from the pending one', () => {
    useSummaries.getState().set('c1', pending)

    useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'ready', summary: '- decided' })

    expect(useSummaries.getState().byConversation.c1).toEqual({
      status: 'ready',
      jobId: 'job-1',
      summary: '- decided',
      messageCount: 12,
      truncated: true,
      at: expect.any(String),
    })
  })

  it('records a failure with its error code', () => {
    useSummaries.getState().set('c1', pending)

    useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'failed', errorCode: 'timeout' })

    expect(useSummaries.getState().byConversation.c1).toEqual({ status: 'failed', errorCode: 'timeout' })
  })

  it('ignores answers for another job, for a settled view, or still pending ones', () => {
    useSummaries.getState().set('c1', pending)
    useSummaries.getState().finished('c1', { jobId: 'other-job', status: 'ready', summary: 'x' })
    useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'pending' })
    expect(useSummaries.getState().byConversation.c1).toEqual(pending)

    useSummaries.getState().set('c2', { status: 'empty' })
    useSummaries.getState().finished('c2', { jobId: 'job-1', status: 'ready', summary: 'x' })
    expect(useSummaries.getState().byConversation.c2).toEqual({ status: 'empty' })
  })

  it('keeps every result until it is cleared, one conversation at a time', () => {
    useSummaries.getState().set('a', pending)
    useSummaries.getState().set('b', { status: 'failed', errorCode: 'internal' })

    useSummaries.getState().clear('b')

    expect(useSummaries.getState().byConversation.a).toEqual(pending)
    expect(useSummaries.getState().byConversation.b).toBeUndefined()
  })
})

describe('viewFromJob', () => {
  it('maps every status and refuses incomplete ones', () => {
    expect(viewFromJob({ jobId: null, status: 'empty' })).toEqual({ status: 'empty' })
    expect(viewFromJob({ jobId: 'j', status: 'pending', messageCount: 3 })).toMatchObject({ status: 'pending', jobId: 'j', messageCount: 3 })
    expect(viewFromJob({ jobId: 'j', status: 'failed' })).toEqual({ status: 'failed', errorCode: 'internal' })
    expect(viewFromJob({ jobId: 'j', status: 'ready', summary: null })).toBeNull()
    expect(viewFromJob({ jobId: null, status: 'pending' })).toBeNull()
    const now = new Date('2026-11-10T21:16:00Z')
    expect(viewFromJob({ jobId: 'j', status: 'ready', summary: '- ok', messageCount: 5 }, now)).toEqual({
      status: 'ready',
      jobId: 'j',
      summary: '- ok',
      messageCount: 5,
      truncated: false,
      at: '2026-11-10T21:16:00.000Z',
    })
  })
})

describe('summary error codes', () => {
  it('shows unknown server codes as a generic failure', () => {
    expect(toSummaryErrorCode('timeout')).toBe('timeout')
    expect(toSummaryErrorCode('from_a_newer_server')).toBe('internal')
    expect(toSummaryErrorCode(null)).toBe('internal')
  })

  it('derives the reason from the HTTP status when starting fails', () => {
    const failure = (status: number) => new ApiError(status, { status, title: 'x' })
    expect(startErrorCode(failure(429))).toBe('rate_limited')
    expect(startErrorCode(failure(409))).toBe('busy')
    expect(startErrorCode(failure(404))).toBe('unavailable')
    expect(startErrorCode(failure(500))).toBe('internal')
    expect(startErrorCode(new Error('network'))).toBe('internal')
  })
})
