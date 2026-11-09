import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MessageBubble, type MessageActions } from './MessageBubble'
import { message } from '../test/factories'

const actions = (): MessageActions => ({
  onReply: vi.fn(),
  onEdit: vi.fn(),
  onDelete: vi.fn(),
  onInfo: vi.fn(),
  onHistory: vi.fn(),
  onRetry: vi.fn(),
  onJumpTo: vi.fn(),
})

describe('MessageBubble', () => {
  it('renders message bodies as text, never as HTML', () => {
    const body = '<img src=x onerror="alert(1)"><b>bold</b>'
    const { container } = render(<MessageBubble message={message({ body })} me="bob" showAuthor {...actions()} />)
    expect(screen.getByText(body)).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
  })

  it('shows read ticks on my own messages', () => {
    render(<MessageBubble message={message({ status: 'Read' })} me="alice" showAuthor={false} {...actions()} />)
    expect(screen.getByLabelText('прочитано')).toBeInTheDocument()
  })

  it('shows the deleted placeholder without actions', () => {
    render(<MessageBubble message={message({ isDeleted: true, body: '[Message deleted]' })} me="alice" showAuthor={false} {...actions()} />)
    expect(screen.getByText('[Повідомлення видалено]')).toBeInTheDocument()
    expect(screen.queryByRole('toolbar')).toBeNull()
  })

  it('offers retry for failed sends', async () => {
    const handlers = actions()
    render(<MessageBubble message={{ ...message(), pending: 'failed' }} me="alice" showAuthor={false} {...handlers} />)
    await userEvent.click(screen.getByRole('button', { name: /повторити/ }))
    expect(handlers.onRetry).toHaveBeenCalled()
  })

  it('only lets the author edit or delete', () => {
    render(<MessageBubble message={message({ createdAt: new Date().toISOString() })} me="bob" showAuthor {...actions()} />)
    expect(screen.queryByRole('button', { name: 'редагувати' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'видалити' })).toBeNull()
    expect(screen.getByRole('button', { name: 'відповісти' })).toBeInTheDocument()
  })
})
