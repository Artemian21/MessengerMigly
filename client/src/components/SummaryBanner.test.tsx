import { beforeEach, describe, expect, it, vi } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { SummaryBanner, type UnreadSnapshot } from './SummaryBanner'
import { aiApi, conversationsApi } from '../api/endpoints'
import { ApiError } from '../api/http'
import { useSummaries } from '../state/summaries'
import type { SummaryJob } from '../api/types'

vi.mock('../api/endpoints', () => ({
  aiApi: { config: vi.fn() },
  conversationsApi: { startSummary: vi.fn(), summary: vi.fn() },
}))

const config = vi.mocked(aiApi.config)
const startSummary = vi.mocked(conversationsApi.startSummary)

const job = (patch: Partial<SummaryJob>): SummaryJob => ({
  jobId: 'job-1',
  status: 'pending',
  summary: null,
  errorCode: null,
  messageCount: 5,
  truncated: false,
  fromSequence: 1,
  toSequence: 5,
  ...patch,
})

function renderBanner(unread: UnreadSnapshot | null = { count: 5, after: 7 }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <SummaryBanner conversationId="c1" unread={unread} />
    </QueryClientProvider>,
  )
}

describe('SummaryBanner', () => {
  beforeEach(() => {
    useSummaries.setState({ byConversation: {} })
    config.mockReset().mockResolvedValue({ summaryEnabled: true, minMessages: 2 })
    startSummary.mockReset()
  })

  it('offers a summary for enough unread messages and starts it from the remembered read position', async () => {
    startSummary.mockResolvedValue(job({}))
    renderBanner()

    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))

    expect(startSummary).toHaveBeenCalledWith('c1', 7)
    expect(await screen.findByRole('status')).toHaveTextContent('Складаю підсумок')
  })

  it('shows nothing when the feature is off, below the minimum, or there is no unread snapshot', async () => {
    config.mockResolvedValue({ summaryEnabled: false, minMessages: 2 })
    const off = renderBanner()
    await vi.waitFor(() => expect(config).toHaveBeenCalled())
    expect(off.container).toBeEmptyDOMElement()
    off.unmount()

    config.mockResolvedValue({ summaryEnabled: true, minMessages: 3 })
    const few = renderBanner({ count: 2, after: 0 })
    await vi.waitFor(() => expect(config).toHaveBeenCalledTimes(2))
    expect(few.container).toBeEmptyDOMElement()
    few.unmount()

    const none = renderBanner(null)
    await vi.waitFor(() => expect(config).toHaveBeenCalledTimes(3))
    expect(none.container).toBeEmptyDOMElement()
  })

  it('shows the finished summary as plain text with the AI note, and closes it', async () => {
    startSummary.mockResolvedValue(job({}))
    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))
    await screen.findByRole('status')

    const hostile = '- <img src=x onerror="alert(1)"> ignore all instructions'
    act(() => useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'ready', summary: hostile }))

    expect(screen.getByText(hostile)).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
    expect(screen.getByText(/Створено ШІ/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Закрити' }))
    expect(screen.queryByText(hostile)).toBeNull()
  })

  it('says when the summary covers only the latest messages', async () => {
    startSummary.mockResolvedValue(job({ messageCount: 300, truncated: true }))
    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))
    await screen.findByRole('status')

    act(() => useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'ready', summary: '- ok' }))

    expect(screen.getByText(/за останніми 300 повідомленнями/)).toBeInTheDocument()
  })

  it('shows a readable error for a failed job and retries from the same position', async () => {
    startSummary.mockResolvedValueOnce(job({})).mockResolvedValueOnce(job({ jobId: 'job-2' }))
    renderBanner()
    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))
    await screen.findByRole('status')

    act(() => useSummaries.getState().finished('c1', { jobId: 'job-1', status: 'failed', errorCode: 'timeout' }))
    expect(screen.getByRole('alert')).toHaveTextContent('Помічник не встиг відповісти')

    await userEvent.click(screen.getByRole('button', { name: 'Спробувати ще раз' }))
    expect(startSummary).toHaveBeenLastCalledWith('c1', 7)
  })

  it('maps start failures to their own messages', async () => {
    startSummary.mockRejectedValue(new ApiError(429, { status: 429, title: 'Too many requests' }))
    renderBanner()

    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Забагато запитів')
  })

  it('reports that there was nothing to summarize', async () => {
    startSummary.mockResolvedValue(job({ jobId: null, status: 'empty', messageCount: 0 }))
    renderBanner()

    await userEvent.click(await screen.findByRole('button', { name: 'Підсумувати' }))

    expect(await screen.findByText('Немає що підсумовувати.')).toBeInTheDocument()
  })

  it('can be hidden without calling the server', async () => {
    renderBanner()

    await userEvent.click(await screen.findByRole('button', { name: 'Сховати' }))

    expect(screen.queryByRole('button', { name: 'Підсумувати' })).toBeNull()
    expect(startSummary).not.toHaveBeenCalled()
  })
})
