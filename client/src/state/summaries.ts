import { create } from 'zustand'
import type { SummaryJob } from '../api/types'

/** What the chat shows for "summarize what I missed". Ephemeral: not persisted anywhere. */
export type SummaryView =
  | { status: 'pending'; jobId: string; messageCount: number; truncated: boolean }
  | { status: 'ready'; jobId: string; summary: string; messageCount: number; truncated: boolean }
  | { status: 'failed'; errorCode: string }
  | { status: 'empty' }

/** The job fields that matter, from a REST response or the SummaryCompleted event. */
type JobUpdate = Pick<SummaryJob, 'jobId' | 'status'> & Partial<Pick<SummaryJob, 'summary' | 'errorCode' | 'messageCount' | 'truncated'>>

export function viewFromJob(job: JobUpdate): SummaryView | null {
  const messageCount = job.messageCount ?? 0
  const truncated = job.truncated ?? false
  if (job.status === 'empty') return { status: 'empty' }
  if (!job.jobId) return null
  if (job.status === 'pending') return { status: 'pending', jobId: job.jobId, messageCount, truncated }
  if (job.status === 'ready' && job.summary) return { status: 'ready', jobId: job.jobId, summary: job.summary, messageCount, truncated }
  if (job.status === 'failed') return { status: 'failed', errorCode: job.errorCode ?? 'internal' }
  return null
}

interface SummariesState {
  byConversation: Record<string, SummaryView | undefined>
  set: (conversationId: string, view: SummaryView) => void
  clear: (conversationId: string) => void
  /** Drops a finished result but keeps a job that is still running (the user may have left the chat meanwhile). */
  clearSettled: (conversationId: string) => void
  /** Applies a finished job. Only the job that is currently pending counts; anything else is a stale or duplicate answer. */
  finished: (conversationId: string, job: JobUpdate) => void
}

export const useSummaries = create<SummariesState>((set, get) => ({
  byConversation: {},
  set: (conversationId, view) => set((s) => ({ byConversation: { ...s.byConversation, [conversationId]: view } })),
  clear: (conversationId) => set((s) => ({ byConversation: { ...s.byConversation, [conversationId]: undefined } })),
  clearSettled: (conversationId) => {
    if (get().byConversation[conversationId]?.status !== 'pending') get().clear(conversationId)
  },
  finished: (conversationId, job) => {
    const current = get().byConversation[conversationId]
    if (current?.status !== 'pending' || current.jobId !== job.jobId) return
    const next = viewFromJob({ ...job, messageCount: current.messageCount, truncated: current.truncated })
    if (next && next.status !== 'pending') get().set(conversationId, next)
  },
}))
