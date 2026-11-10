import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { clearAllSummaries, initSummaryPersistence, loadSummaries, saveSummaries } from './summaryStorage'
import { useSummaries, type SummaryView } from './summaries'
import { useAuth } from './auth'
import type { AuthResponse, Me } from '../api/types'

const ready: SummaryView = { status: 'ready', jobId: 'j1', summary: '- decided', messageCount: 5, truncated: false, at: '2026-11-10T21:16:00.000Z' }
const user = (id: string) => ({ id }) as Me
const signIn = (id: string) => useAuth.getState().signedIn({ accessToken: 't', expiresAt: '2099-01-01T00:00:00Z', user: user(id) } as AuthResponse)

describe('summary storage', () => {
  beforeEach(() => sessionStorage.clear())

  it('round-trips what was saved, per user', () => {
    saveSummaries('alice', { c1: ready, c2: { status: 'failed', errorCode: 'timeout' } })
    saveSummaries('bob', { c1: { status: 'empty' } })

    expect(loadSummaries('alice')).toEqual({ c1: ready, c2: { status: 'failed', errorCode: 'timeout' } })
    expect(loadSummaries('bob')).toEqual({ c1: { status: 'empty' } })
    expect(loadSummaries('nobody')).toEqual({})
  })

  it('does not store a request that has no job id yet, and removes the key when nothing is left', () => {
    saveSummaries('alice', { c1: { status: 'pending', jobId: '', messageCount: 0, truncated: false }, c2: undefined })
    expect(sessionStorage.getItem('migly.summaries.alice')).toBeNull()

    saveSummaries('alice', { c1: { status: 'pending', jobId: 'j9', messageCount: 3, truncated: false } })
    expect(loadSummaries('alice').c1).toMatchObject({ status: 'pending', jobId: 'j9' })
  })

  it('survives corrupt, foreign or hand-edited storage', () => {
    sessionStorage.setItem('migly.summaries.alice', '{not json')
    expect(loadSummaries('alice')).toEqual({})

    sessionStorage.setItem('migly.summaries.alice', JSON.stringify({ v: 99, byConversation: { c1: ready } }))
    expect(loadSummaries('alice')).toEqual({})

    sessionStorage.setItem(
      'migly.summaries.alice',
      JSON.stringify({ v: 1, byConversation: { good: ready, noAt: { ...ready, at: undefined }, wrong: { status: 'ready', summary: 5 }, junk: 'x' } }),
    )
    expect(loadSummaries('alice')).toEqual({ good: ready })
  })

  it('clearAllSummaries removes every user but leaves other keys alone', () => {
    saveSummaries('alice', { c1: ready })
    saveSummaries('bob', { c1: ready })
    sessionStorage.setItem('unrelated', 'keep')

    clearAllSummaries()

    expect(sessionStorage.getItem('migly.summaries.alice')).toBeNull()
    expect(sessionStorage.getItem('migly.summaries.bob')).toBeNull()
    expect(sessionStorage.getItem('unrelated')).toBe('keep')
  })
})

describe('summary persistence wiring', () => {
  let stop: () => void

  beforeEach(() => {
    sessionStorage.clear()
    useSummaries.setState({ byConversation: {} })
    useAuth.setState({ accessToken: null, expiresAt: 0, user: null, status: 'loading' })
    stop = initSummaryPersistence()
  })
  afterEach(() => stop())

  it('saves changes for the signed-in user and restores them after a reload', () => {
    signIn('alice')
    useSummaries.getState().set('c1', ready)
    expect(loadSummaries('alice')).toEqual({ c1: ready })

    // reload: a fresh app has an empty store and an unknown user until the session bootstraps
    stop()
    useSummaries.setState({ byConversation: {} })
    useAuth.setState({ accessToken: null, expiresAt: 0, user: null, status: 'loading' })
    stop = initSummaryPersistence()
    expect(useSummaries.getState().byConversation).toEqual({})

    signIn('alice')
    expect(useSummaries.getState().byConversation).toEqual({ c1: ready })
  })

  it('removes a closed summary from storage', () => {
    signIn('alice')
    useSummaries.getState().set('c1', ready)

    useSummaries.getState().clear('c1')

    expect(sessionStorage.getItem('migly.summaries.alice')).toBeNull()
  })

  it('does not show one user the summaries of another', () => {
    signIn('alice')
    useSummaries.getState().set('c1', ready)

    signIn('bob')

    expect(useSummaries.getState().byConversation).toEqual({})
    expect(loadSummaries('alice')).toEqual({ c1: ready }) // still there for alice until the session ends
  })

  it('wipes everything when the session ends, including a failed bootstrap after a reload', () => {
    signIn('alice')
    useSummaries.getState().set('c1', ready)

    useAuth.getState().signedOut()

    expect(useSummaries.getState().byConversation).toEqual({})
    expect(sessionStorage.getItem('migly.summaries.alice')).toBeNull()
  })

  it('does not wipe storage while the session is still bootstrapping', () => {
    saveSummaries('alice', { c1: ready })
    stop()
    useSummaries.setState({ byConversation: {} })
    useAuth.setState({ accessToken: null, expiresAt: 0, user: null, status: 'loading' })

    stop = initSummaryPersistence() // page just loaded: status is "loading", the user is not known yet

    expect(loadSummaries('alice')).toEqual({ c1: ready })
  })
})
