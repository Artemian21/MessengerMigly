import type { AuthResponse, ProblemDetails } from './types'
import { useAuth } from '../state/auth'
import { useConnectivity } from '../state/connectivity'
import { useLocale } from '../i18n'

export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Request failed (${status})`)
    this.status = status
    this.problem = problem
  }

  /** First validation message for a field (422), if any. */
  field(name: string): string | undefined {
    return this.problem.errors?.[name]?.[0]
  }
}

const WAKING_THRESHOLD_MS = 3000

let refreshInFlight: Promise<AuthResponse | null> | null = null

/**
 * One shared refresh at a time: parallel 401s (or several tabs' bootstraps) wait for the same promise,
 * so the rotating refresh cookie is used once per rotation.
 */
export function refreshSession(): Promise<AuthResponse | null> {
  refreshInFlight ??= (async () => {
    try {
      const response = await rawFetch('/api/v1/auth/refresh', {
        method: 'POST',
        headers: { 'X-Requested-With': 'XMLHttpRequest', 'Accept-Language': useLocale.getState().locale },
        credentials: 'same-origin',
      })
      if (!response.ok) {
        useAuth.getState().signedOut()
        return null
      }
      const body = (await response.json()) as AuthResponse
      useAuth.getState().signedIn(body)
      return body
    } catch {
      return null
    } finally {
      refreshInFlight = null
    }
  })()
  return refreshInFlight
}

/** fetch with "server is waking up" detection for free-tier cold starts (OPS-07). */
async function rawFetch(url: string, init: RequestInit): Promise<Response> {
  const connectivity = useConnectivity.getState()
  const timer = setTimeout(() => connectivity.setWaking(true), WAKING_THRESHOLD_MS)
  try {
    for (let attempt = 0; ; attempt++) {
      try {
        const response = await fetch(url, init)
        if ((response.status === 502 || response.status === 503) && attempt < 4 && init.method === 'GET') {
          connectivity.setWaking(true)
          await delay(2000 * (attempt + 1))
          continue
        }
        connectivity.setWaking(false)
        return response
      } catch (error) {
        if (attempt >= 4) throw error
        connectivity.setWaking(true)
        await delay(2000 * (attempt + 1))
      }
    }
  } finally {
    clearTimeout(timer)
  }
}

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export interface RequestOptions {
  method?: string
  body?: unknown
  anonymous?: boolean
  signal?: AbortSignal
}

export async function api<T>(url: string, options: RequestOptions = {}): Promise<T> {
  const send = (token: string | null) => {
    const headers: Record<string, string> = { Accept: 'application/json', 'Accept-Language': useLocale.getState().locale }
    if (options.body !== undefined) headers['Content-Type'] = 'application/json'
    if (token && !options.anonymous) headers.Authorization = `Bearer ${token}`
    return rawFetch(url, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: 'same-origin',
      signal: options.signal,
    })
  }

  let token = useAuth.getState().accessToken
  if (token && !options.anonymous && useAuth.getState().isExpiringSoon()) {
    token = (await refreshSession())?.accessToken ?? null
  }

  let response = await send(token)
  if (response.status === 401 && !options.anonymous && token) {
    const refreshed = await refreshSession()
    if (!refreshed) throw await toError(response)
    response = await send(refreshed.accessToken)
  }

  if (!response.ok) throw await toError(response)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

async function toError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = { status: response.status, title: response.statusText }
  try {
    const text = await response.text()
    if (text) problem = JSON.parse(text) as ProblemDetails
  } catch {
    // Non-JSON error body: keep the status line.
  }
  return new ApiError(response.status, problem)
}

/** Access token for SignalR's accessTokenFactory: refreshed proactively when close to expiry. */
export async function currentAccessToken(): Promise<string> {
  const state = useAuth.getState()
  if (!state.accessToken || state.isExpiringSoon()) {
    return (await refreshSession())?.accessToken ?? ''
  }
  return state.accessToken
}
