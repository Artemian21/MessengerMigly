import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { conversationsApi } from '../api/endpoints'
import type { UserSummary } from '../api/types'
import { conversationsKey } from '../lib/messages'
import { fieldError } from '../lib/errors'
import { UserPicker } from '../components/UserPicker'
import { ErrorNotice, Field } from '../components/ui'
import { useT } from '../i18n'

export function NewChatPage() {
  const t = useT()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [mode, setMode] = useState<'direct' | 'group'>('direct')
  const [title, setTitle] = useState('')
  const [members, setMembers] = useState<UserSummary[]>([])
  const create = useMutation({
    mutationFn: () => conversationsApi.createGroup(title, members.map((m) => m.id)),
    onSuccess: async (group) => {
      await queryClient.invalidateQueries({ queryKey: conversationsKey })
      navigate(`/chats/${group.id}`)
    },
  })

  return (
    <div className="page">
      <h2>{t('new.title')}</h2>
      <div className="row" role="tablist" style={{ marginBottom: 16 }}>
        <button role="tab" aria-selected={mode === 'direct'} className={`btn small${mode === 'direct' ? ' primary' : ''}`} onClick={() => setMode('direct')}>
          {t('new.personal')}
        </button>
        <button role="tab" aria-selected={mode === 'group'} className={`btn small${mode === 'group' ? ' primary' : ''}`} onClick={() => setMode('group')}>
          {t('new.group')}
        </button>
      </div>
      {mode === 'direct' ? (
        <UserPicker actionLabel={t('new.message')} onPick={(u) => navigate(`/direct/${u.id}`)} />
      ) : (
        <form
          onSubmit={(e) => {
            e.preventDefault()
            create.mutate()
          }}
        >
          <Field label={t('new.groupName')} error={fieldError(create.error, 'title')}>
            {(id) => <input id={id} className="input" value={title} onChange={(e) => setTitle(e.target.value)} placeholder={t('new.groupPlaceholder')} />}
          </Field>
          <p className="muted">{t('new.membersHint')}</p>
          <div>
            {members.map((m) => (
              <span key={m.id} className="chip">
                {m.displayName}
                <button type="button" className="btn small ghost" aria-label={t('new.removeMember', { name: m.displayName })} onClick={() => setMembers(members.filter((x) => x.id !== m.id))}>
                  ✕
                </button>
              </span>
            ))}
          </div>
          {fieldError(create.error, 'memberIds') ? <p className="field-error">{fieldError(create.error, 'memberIds')}</p> : null}
          <UserPicker actionLabel={t('common.add')} exclude={members.map((m) => m.id)} onPick={(u) => setMembers([...members, u])} />
          <ErrorNotice error={create.error && !fieldError(create.error, 'title') && !fieldError(create.error, 'memberIds') ? create.error : null} />
          <button className="btn primary" type="submit" disabled={create.isPending || members.length < 2 || !title.trim()} style={{ marginTop: 16 }}>
            {t('new.create')}
          </button>
        </form>
      )}
    </div>
  )
}
