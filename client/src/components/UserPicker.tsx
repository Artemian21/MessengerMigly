import { useDeferredValue, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { usersApi } from '../api/endpoints'
import type { UserSummary } from '../api/types'
import { Avatar } from './ui'
import { useT } from '../i18n'

/** USR-03: prefix search by username or display name (min. 2 characters). */
export function UserPicker({
  onPick,
  exclude = [],
  actionLabel,
}: {
  onPick: (user: UserSummary) => void
  exclude?: string[]
  actionLabel?: string
}) {
  const t = useT()
  const [term, setTerm] = useState('')
  const deferred = useDeferredValue(term.trim())
  const results = useQuery({
    queryKey: ['user-search', deferred],
    queryFn: () => usersApi.search(deferred),
    enabled: deferred.length >= 2,
  })
  const items = (results.data?.items ?? []).filter((u) => !exclude.includes(u.id))
  return (
    <div>
      <label htmlFor="user-search" className="sr-only">
        {t('picker.label')}
      </label>
      <input
        id="user-search"
        className="input"
        placeholder={t('picker.placeholder')}
        value={term}
        onChange={(e) => setTerm(e.target.value)}
        autoComplete="off"
      />
      {deferred.length >= 2 && results.isSuccess && items.length === 0 ? <p className="muted">{t('picker.nobody')}</p> : null}
      <ul className="list" aria-live="polite">
        {items.map((u) => (
          <li key={u.id}>
            <Avatar name={u.displayName} small />
            <span className="spacer">
              {u.displayName} <span className="muted">@{u.username}</span>
            </span>
            <button className="btn small" type="button" onClick={() => onPick(u)}>
              {actionLabel ?? t('common.select')}
            </button>
          </li>
        ))}
      </ul>
    </div>
  )
}
