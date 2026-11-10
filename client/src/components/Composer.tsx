import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import type { UiMessage } from '../api/types'
import { useT } from '../i18n'

const MAX_LENGTH = 4000
const TYPING_THROTTLE_MS = 2500

interface Props {
  disabled?: boolean
  disabledReason?: string
  replyTo: UiMessage | null
  editing: UiMessage | null
  onCancelContext: () => void
  onSend: (body: string) => void
  onEdit: (message: UiMessage, body: string) => void
  onTyping?: () => void
}

function graphemeLength(text: string): number {
  const Segmenter = (Intl as typeof Intl & { Segmenter?: typeof Intl.Segmenter }).Segmenter
  if (!Segmenter) return [...text].length
  let count = 0
  for (const segment of new Segmenter(undefined, { granularity: 'grapheme' }).segment(text)) {
    void segment
    count++
  }
  return count
}

/** Remount with `key={editing?.id}` to load the text of the message being edited. */
export function Composer({ disabled, disabledReason, replyTo, editing, onCancelContext, onSend, onEdit, onTyping }: Props) {
  const t = useT()
  const [text, setText] = useState(editing?.body ?? '')
  const lastTyping = useRef(0)
  const inputRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    if (editing || replyTo) inputRef.current?.focus()
  }, [editing, replyTo])

  const trimmed = text.trim()
  const length = graphemeLength(trimmed)
  const tooLong = length > MAX_LENGTH
  const canSubmit = !disabled && trimmed.length > 0 && !tooLong

  const submit = (event?: FormEvent) => {
    event?.preventDefault()
    if (!canSubmit) return
    if (editing) onEdit(editing, trimmed)
    else onSend(trimmed)
    setText('')
  }

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault()
      submit()
    } else if (event.key === 'Escape' && (editing || replyTo)) {
      onCancelContext()
      if (editing) setText('')
    }
  }

  const onChange = (value: string) => {
    setText(value)
    const now = Date.now()
    if (onTyping && value.trim() && now - lastTyping.current > TYPING_THROTTLE_MS) {
      lastTyping.current = now
      onTyping()
    }
  }

  if (disabled) {
    return (
      <div className="composer muted" role="status">
        {disabledReason ?? t('chat.cannotSend')}
      </div>
    )
  }

  return (
    <form className="composer" onSubmit={submit}>
      {replyTo || editing ? (
        <div className="composer-context">
          <span>
            {editing ? t('composer.editing') : t('composer.replyingTo', { name: replyTo!.sender.displayName })}
            {!editing ? <em>{(replyTo!.body ?? '').slice(0, 80)}</em> : null}
          </span>
          <span className="spacer" />
          <button
            type="button"
            className="btn small ghost"
            onClick={() => {
              onCancelContext()
              if (editing) setText('')
            }}
          >
            {t('common.cancel')}
          </button>
        </div>
      ) : null}
      <div className="composer-row">
        <label htmlFor="composer-input" className="sr-only">
          {t('composer.label')}
        </label>
        <textarea
          id="composer-input"
          ref={inputRef}
          className="textarea"
          rows={Math.min(6, Math.max(1, text.split('\n').length))}
          value={text}
          placeholder={t('composer.placeholder')}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={onKeyDown}
          aria-invalid={tooLong}
        />
        <button className="btn primary" type="submit" disabled={!canSubmit}>
          {editing ? t('composer.save') : t('composer.send')}
        </button>
      </div>
      {length > MAX_LENGTH - 200 ? (
        <div className={`counter${tooLong ? ' over' : ''}`}>
          {length}/{MAX_LENGTH}
        </div>
      ) : null}
    </form>
  )
}
