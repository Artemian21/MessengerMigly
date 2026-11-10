import { useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Navigate } from 'react-router-dom'
import { adminApi } from '../api/endpoints'
import { useAuth } from '../state/auth'
import { formatDay, formatTime } from '../lib/format'
import { ErrorNotice } from '../components/ui'
import { useT } from '../i18n'

const PAGE = 20

/** RBAC-01: account administration and audit log. There is deliberately no way to read message content here. */
export function AdminPage() {
  const t = useT()
  const role = useAuth((s) => s.user?.role)
  const queryClient = useQueryClient()
  const [term, setTerm] = useState('')
  const [offset, setOffset] = useState(0)
  const [auditOffset, setAuditOffset] = useState(0)
  const users = useQuery({ queryKey: ['admin-users', term, offset], queryFn: () => adminApi.users(term, PAGE, offset), placeholderData: keepPreviousData })
  const audit = useQuery({ queryKey: ['admin-audit', auditOffset], queryFn: () => adminApi.audit(PAGE, auditOffset), placeholderData: keepPreviousData })
  const toggle = useMutation({
    mutationFn: ({ id, suspend }: { id: string; suspend: boolean }) => (suspend ? adminApi.suspend(id) : adminApi.unsuspend(id)),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin-users'] })
      void queryClient.invalidateQueries({ queryKey: ['admin-audit'] })
    },
  })

  if (role !== 'Admin') return <Navigate to="/" replace />

  return (
    <div className="page">
      <h2>{t('admin.title')}</h2>
      <section className="card">
        <h3>{t('admin.users')}</h3>
        <label htmlFor="admin-search" className="sr-only">
          {t('admin.filter')}
        </label>
        <input
          id="admin-search"
          className="input"
          placeholder={t('admin.filter')}
          value={term}
          onChange={(e) => {
            setTerm(e.target.value)
            setOffset(0)
          }}
        />
        <ErrorNotice error={users.error ?? toggle.error} />
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr>
                <th>{t('admin.user')}</th>
                <th>{t('admin.email')}</th>
                <th>{t('admin.status')}</th>
                <th>{t('admin.joined')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(users.data?.items ?? []).map((u) => (
                <tr key={u.id}>
                  <td>
                    {u.displayName}
                    <br />
                    <span className="faint">@{u.username}</span> {u.role === 'Admin' ? <span className="badge">{t('admin.adminBadge')}</span> : null}
                  </td>
                  <td>{u.email}</td>
                  <td>
                    <span className={`badge${u.status === 'Suspended' ? ' warn' : ''}`}>{t(`admin.status.${u.status}`)}</span>
                    {!u.emailConfirmed ? <span className="badge">{t('admin.unverified')}</span> : null}
                  </td>
                  <td>{formatDay(u.createdAt)}</td>
                  <td>
                    {u.role !== 'Admin' ? (
                      <button className={`btn small${u.status === 'Active' ? ' danger' : ''}`} onClick={() => toggle.mutate({ id: u.id, suspend: u.status === 'Active' })}>
                        {u.status === 'Active' ? t('admin.suspend') : t('admin.unsuspend')}
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Pager total={users.data?.total ?? 0} offset={offset} next={users.data?.next ?? null} onChange={setOffset} />
      </section>
      <section className="card">
        <h3>{t('admin.audit')}</h3>
        <ErrorNotice error={audit.error} />
        <div style={{ overflowX: 'auto' }}>
          <table className="table">
            <thead>
              <tr>
                <th>{t('admin.when')}</th>
                <th>{t('admin.action')}</th>
                <th>{t('admin.target')}</th>
                <th>{t('admin.actor')}</th>
                <th>{t('admin.ip')}</th>
              </tr>
            </thead>
            <tbody>
              {(audit.data?.items ?? []).map((a) => (
                <tr key={a.id}>
                  <td>
                    {formatDay(a.createdAt)} {formatTime(a.createdAt)}
                  </td>
                  <td>{a.action}</td>
                  <td className="faint">
                    {a.entityType} {a.entityId?.slice(0, 8)}
                  </td>
                  <td className="faint">{a.actorUserId?.slice(0, 8) ?? '—'}</td>
                  <td className="faint">{a.ip ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <Pager total={audit.data?.total ?? 0} offset={auditOffset} next={audit.data?.next ?? null} onChange={setAuditOffset} />
      </section>
    </div>
  )
}

function Pager({ total, offset, next, onChange }: { total: number; offset: number; next: number | null; onChange: (offset: number) => void }) {
  const t = useT()
  return (
    <div className="row" style={{ marginTop: 8 }}>
      <span className="muted">
        {t('admin.range', { from: total === 0 ? 0 : offset + 1, to: Math.min(offset + PAGE, total), total })}
      </span>
      <span className="spacer" />
      <button className="btn small" disabled={offset === 0} onClick={() => onChange(Math.max(0, offset - PAGE))}>
        {t('admin.prev')}
      </button>
      <button className="btn small" disabled={next === null} onClick={() => next !== null && onChange(next)}>
        {t('admin.next')}
      </button>
    </div>
  )
}
