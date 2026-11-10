import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { messagesApi } from '../api/endpoints'
import { formatDay, formatTime } from '../lib/format'
import { ErrorNotice } from '../components/ui'
import { useT } from '../i18n'

const PAGE = 20

/** MSG-07: search only covers messages you can currently see. */
export function SearchPage() {
  const t = useT()
  const [input, setInput] = useState('')
  const [term, setTerm] = useState('')
  const [offset, setOffset] = useState(0)
  const results = useQuery({
    queryKey: ['search', term, offset],
    queryFn: () => messagesApi.search(term, undefined, PAGE, offset),
    enabled: term.length >= 2,
  })

  return (
    <div className="page">
      <h2>{t('search.title')}</h2>
      <form
        className="row"
        onSubmit={(e) => {
          e.preventDefault()
          setOffset(0)
          setTerm(input.trim())
        }}
      >
        <label htmlFor="search-input" className="sr-only">
          {t('search.label')}
        </label>
        <input id="search-input" className="input" placeholder={t('search.placeholder')} value={input} onChange={(e) => setInput(e.target.value)} />
        <button className="btn primary" type="submit" disabled={input.trim().length < 2}>
          {t('search.button')}
        </button>
      </form>
      <ErrorNotice error={results.error} />
      {results.data ? (
        <>
          <p className="muted" role="status">
            {t('search.results', { count: results.data.total })}
          </p>
          {results.data.items.map((hit) => (
            <Link key={hit.messageId} className="search-hit" to={`/chats/${hit.conversationId}`}>
              <div className="row">
                <strong>{hit.sender.displayName}</strong>
                {hit.conversationTitle ? <span className="badge"># {hit.conversationTitle}</span> : null}
                <span className="spacer" />
                <span className="faint">
                  {formatDay(hit.createdAt)} {formatTime(hit.createdAt)}
                </span>
              </div>
              <Highlighted text={hit.snippet} term={term} />
            </Link>
          ))}
          <div className="row" style={{ marginTop: 12 }}>
            <button className="btn small" disabled={offset === 0} onClick={() => setOffset(Math.max(0, offset - PAGE))}>
              {t('search.newer')}
            </button>
            <button className="btn small" disabled={results.data.next === null} onClick={() => setOffset(results.data!.next ?? offset)}>
              {t('search.older')}
            </button>
          </div>
        </>
      ) : null}
    </div>
  )
}

/** Highlights matches without injecting HTML. */
function Highlighted({ text, term }: { text: string; term: string }) {
  const lower = text.toLowerCase()
  const needle = term.toLowerCase()
  const parts: { text: string; match: boolean }[] = []
  let index = 0
  while (needle && index < text.length) {
    const found = lower.indexOf(needle, index)
    if (found < 0) break
    if (found > index) parts.push({ text: text.slice(index, found), match: false })
    parts.push({ text: text.slice(found, found + needle.length), match: true })
    index = found + needle.length
  }
  parts.push({ text: text.slice(index), match: false })
  return <div className="bubble-text">{parts.map((p, i) => (p.match ? <mark key={i}>{p.text}</mark> : <span key={i}>{p.text}</span>))}</div>
}
