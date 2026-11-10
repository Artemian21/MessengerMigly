import { ApiError } from '../api/http'

/** Error codes the UI has a text for (keys summary.error.*). Server job codes plus the ones derived from HTTP statuses. */
export const SUMMARY_ERROR_CODES = ['provider_unavailable', 'timeout', 'internal', 'busy', 'rate_limited', 'unavailable'] as const
export type SummaryErrorCode = (typeof SUMMARY_ERROR_CODES)[number]

/** Anything unknown (for example a code from a newer server) is shown as a generic failure. */
export function toSummaryErrorCode(code: string | null | undefined): SummaryErrorCode {
  return (SUMMARY_ERROR_CODES as readonly string[]).includes(code ?? '') ? (code as SummaryErrorCode) : 'internal'
}

/** Why starting a summary failed: 429 rate limit, 409 queue full, 404 feature switched off. */
export function startErrorCode(error: unknown): SummaryErrorCode {
  if (error instanceof ApiError) {
    if (error.status === 429) return 'rate_limited'
    if (error.status === 409) return 'busy'
    if (error.status === 404) return 'unavailable'
  }
  return 'internal'
}
