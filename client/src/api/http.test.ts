import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, refreshSession } from './http'
import { useAuth } from '../state/auth'

const user = { id: 'u1', email: 'a@example.com', username: 'alice', displayName: 'Alice', role: 'User', emailConfirmed: true, needsProfile: false, hasPassword: true, createdAt: '2026-10-01T00:00:00Z' }

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('http client', () => {
  beforeEach(() => {
    useAuth.setState({ accessToken: 'old', expiresAt: Date.now() + 10 * 60_000, user: user as never, status: 'authenticated' })
  })
  afterEach(() => vi.restoreAllMocks())

  it('refreshes once for parallel 401s and retries with the new token', async () => {
    let refreshCalls = 0
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.endsWith('/auth/refresh')) {
        refreshCalls++
        expect((init?.headers as Record<string, string>)['X-Requested-With']).toBe('XMLHttpRequest')
        expect((init?.headers as Record<string, string>)['Accept-Language']).toBe('uk')
        return json(200, { accessToken: 'new', expiresAt: new Date(Date.now() + 600_000).toISOString(), user })
      }
      const auth = (init?.headers as Record<string, string>).Authorization
      return auth === 'Bearer new' ? json(200, { ok: true }) : json(401, { title: 'Unauthorized' })
    })

    const results = await Promise.all([api<{ ok: boolean }>('/api/v1/a'), api<{ ok: boolean }>('/api/v1/b'), api<{ ok: boolean }>('/api/v1/c')])

    expect(results.every((r) => r.ok)).toBe(true)
    expect(refreshCalls).toBe(1)
    expect(useAuth.getState().accessToken).toBe('new')
  })

  it('signs out when the refresh cookie is rejected', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(401, { title: 'Unauthorized' }))
    expect(await refreshSession()).toBeNull()
    expect(useAuth.getState().status).toBe('anonymous')
  })

  it('surfaces problem details with field errors', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      json(422, { title: 'Validation failed', detail: 'One or more validation errors occurred.', errors: { body: ['Message must not be empty.'] } }),
    )
    const error = await api('/api/v1/conversations/x/messages', { method: 'POST', body: {} }).catch((e) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(422)
    expect((error as ApiError).field('body')).toBe('Message must not be empty.')
  })
})
