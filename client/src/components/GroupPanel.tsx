import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { conversationsApi } from '../api/endpoints'
import type { Conversation } from '../api/types'
import { useAuth } from '../state/auth'
import { useLive } from '../state/live'
import { useSummaries } from '../state/summaries'
import { conversationsKey } from '../lib/messages'
import { formatLastSeen } from '../lib/format'
import { useT } from '../i18n'
import { Avatar, ErrorNotice } from './ui'
import { UserPicker } from './UserPicker'

/** Group settings (CNV-08/10, RBAC-02): owners and admins manage members; only the owner changes roles or deletes. */
export function GroupPanel({ conversation, onClose }: { conversation: Conversation; onClose: () => void }) {
  const t = useT()
  const me = useAuth((s) => s.user?.id)
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const presence = useLive((s) => s.presence)
  const members = useQuery({ queryKey: ['members', conversation.id], queryFn: () => conversationsApi.members(conversation.id) })
  const [title, setTitle] = useState(conversation.title ?? '')
  const [adding, setAdding] = useState(false)
  const canManage = !conversation.isDeleted && (conversation.myRole === 'Owner' || conversation.myRole === 'Admin')
  const isOwner = !conversation.isDeleted && conversation.myRole === 'Owner'

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['members', conversation.id] })
    void queryClient.invalidateQueries({ queryKey: conversationsKey })
    void queryClient.invalidateQueries({ queryKey: ['conversation', conversation.id] })
  }
  const action = useMutation({ mutationFn: (run: () => Promise<unknown>) => run(), onSuccess: refresh })

  const leave = async () => {
    if (!confirm(t('group.confirmLeave'))) return
    await action.mutateAsync(() => conversationsApi.leave(conversation.id))
    useSummaries.getState().clear(conversation.id)
    navigate('/')
  }
  const remove = async () => {
    if (!confirm(t('group.confirmDelete'))) return
    await action.mutateAsync(() => conversationsApi.remove(conversation.id))
    useSummaries.getState().clear(conversation.id)
  }

  return (
    <aside className="side-panel" aria-label={t('group.settings')}>
      <div className="row">
        <h3 style={{ margin: 0 }}>{t('group.title')}</h3>
        <span className="spacer" />
        <button className="btn small ghost icon" onClick={onClose} aria-label={t('group.closeSettings')}>
          ✕
        </button>
      </div>
      <ErrorNotice error={action.error} />
      {canManage ? (
        <form
          className="row"
          style={{ margin: '16px 0' }}
          onSubmit={(e) => {
            e.preventDefault()
            action.mutate(() => conversationsApi.rename(conversation.id, title))
          }}
        >
          <label htmlFor="group-title" className="sr-only">
            {t('group.titleLabel')}
          </label>
          <input id="group-title" className="input" value={title} onChange={(e) => setTitle(e.target.value)} />
          <button className="btn small" type="submit">
            {t('group.rename')}
          </button>
        </form>
      ) : null}
      <p className="muted">{members.data ? t('group.memberCount', { count: members.data.length }) : '…'}</p>
      <ul className="list">
        {(members.data ?? []).map((m) => {
          const live = presence[m.userId]
          const canChangeRole = isOwner && m.role !== 'Owner'
          const canRemove = m.userId !== me && ((isOwner && m.role !== 'Owner') || (conversation.myRole === 'Admin' && m.role === 'Member' && canManage))
          return (
            <li key={m.userId} className="member-row">
              <div className="member-main">
                <Avatar name={m.displayName} small online={live?.online ?? m.online} />
                <div className="member-info">
                  <div className="member-name">
                    {m.displayName} {m.userId === me ? <span className="faint">{t('group.you')}</span> : null}
                  </div>
                  <div className="faint member-meta">
                    @{m.username} · {formatLastSeen(live?.online ?? m.online, live?.lastSeenAt ?? m.lastSeenAt)}
                  </div>
                </div>
                <span className="badge">{t(`group.role.${m.role}`)}</span>
              </div>
              {canChangeRole || canRemove ? (
                <div className="member-actions">
                  {canChangeRole ? (
                    <button
                      className="btn small"
                      onClick={() => action.mutate(() => conversationsApi.changeRole(conversation.id, m.userId, m.role === 'Admin' ? 'Member' : 'Admin'))}
                    >
                      {m.role === 'Admin' ? t('group.demote') : t('group.makeAdmin')}
                    </button>
                  ) : null}
                  {canRemove ? (
                    <button className="btn small danger" onClick={() => action.mutate(() => conversationsApi.removeMember(conversation.id, m.userId))}>
                      {t('group.remove')}
                    </button>
                  ) : null}
                </div>
              ) : null}
            </li>
          )
        })}
      </ul>
      {canManage ? (
        adding ? (
          <div style={{ marginTop: 12 }}>
            <UserPicker
              actionLabel={t('common.add')}
              exclude={(members.data ?? []).map((m) => m.userId)}
              onPick={(u) => action.mutate(() => conversationsApi.addMembers(conversation.id, [u.id]))}
            />
            <button className="btn small ghost" onClick={() => setAdding(false)}>
              {t('common.done')}
            </button>
          </div>
        ) : (
          <button className="btn small" style={{ marginTop: 12 }} onClick={() => setAdding(true)}>
            {t('group.addMembers')}
          </button>
        )
      ) : null}
      <div className="stack" style={{ marginTop: 24 }}>
        <button className="btn danger" onClick={leave}>
          {t('group.leave')}
        </button>
        {isOwner ? (
          <button className="btn danger" onClick={remove}>
            {t('group.delete')}
          </button>
        ) : null}
      </div>
    </aside>
  )
}
