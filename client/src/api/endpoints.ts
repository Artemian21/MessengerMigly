import { api } from './http'
import { useLocale } from '../i18n'
import type {
  AdminUser,
  AiConfig,
  AuditEntry,
  AuthResponse,
  Conversation,
  EditHistoryEntry,
  Me,
  Member,
  Message,
  MessageInfo,
  MessagePage,
  MessageStatusItem,
  Paged,
  PublicUser,
  SearchHit,
  Session,
  SummaryJob,
  UserSummary,
} from './types'

const v1 = '/api/v1'
const q = (params: Record<string, string | number | undefined | null>) => {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') search.set(key, String(value))
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

export const authApi = {
  register: (body: { email: string; password: string; username: string; displayName: string }) =>
    api<void>(`${v1}/auth/register`, { method: 'POST', body, anonymous: true }),
  verifyEmail: (token: string) => api<void>(`${v1}/auth/verify-email`, { method: 'POST', body: { token }, anonymous: true }),
  resendVerification: (email: string) =>
    api<void>(`${v1}/auth/resend-verification`, { method: 'POST', body: { email }, anonymous: true }),
  login: (email: string, password: string) =>
    api<AuthResponse>(`${v1}/auth/login`, { method: 'POST', body: { email, password }, anonymous: true }),
  logout: () =>
    fetch(`${v1}/auth/logout`, { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest', 'Accept-Language': useLocale.getState().locale },
      credentials: 'same-origin',
    }),
  forgotPassword: (email: string) =>
    api<void>(`${v1}/auth/forgot-password`, { method: 'POST', body: { email }, anonymous: true }),
  resetPassword: (token: string, newPassword: string) =>
    api<void>(`${v1}/auth/reset-password`, { method: 'POST', body: { token, newPassword }, anonymous: true }),
  changePassword: (currentPassword: string | null, newPassword: string) =>
    api<void>(`${v1}/auth/change-password`, { method: 'POST', body: { currentPassword, newPassword } }),
  sessions: () => api<Session[]>(`${v1}/auth/sessions`),
  revokeSession: (id: string) => api<void>(`${v1}/auth/sessions/${id}`, { method: 'DELETE' }),
  providers: () => api<{ providers: string[] }>(`${v1}/auth/external/providers`, { anonymous: true }),
  completeProfile: (username: string, displayName?: string) =>
    api<Me>(`${v1}/auth/external/complete-profile`, { method: 'POST', body: { username, displayName } }),
}

export const usersApi = {
  me: () => api<Me>(`${v1}/users/me`),
  updateProfile: (displayName: string) => api<Me>(`${v1}/users/me`, { method: 'PATCH', body: { displayName } }),
  changeUsername: (username: string) => api<Me>(`${v1}/users/me/username`, { method: 'PUT', body: { username } }),
  deleteAccount: (password: string | null) => api<void>(`${v1}/users/me`, { method: 'DELETE', body: { password } }),
  search: (term: string, limit = 20, offset = 0) => api<Paged<UserSummary>>(`${v1}/users${q({ q: term, limit, offset })}`),
  get: (id: string) => api<PublicUser>(`${v1}/users/${id}`),
}

export interface SendInput {
  clientMessageId: string
  body: string
  replyToMessageId?: string | null
}

export const conversationsApi = {
  list: () => api<Conversation[]>(`${v1}/conversations`),
  get: (id: string) => api<Conversation>(`${v1}/conversations/${id}`),
  direct: (peerId: string) => api<Conversation>(`${v1}/conversations/direct/${peerId}`),
  sendDirect: (peerId: string, input: SendInput) =>
    api<Message>(`${v1}/conversations/direct/${peerId}/messages`, { method: 'POST', body: input }),
  createGroup: (title: string, memberIds: string[]) =>
    api<Conversation>(`${v1}/conversations/groups`, { method: 'POST', body: { title, memberIds } }),
  rename: (id: string, title: string) => api<Conversation>(`${v1}/conversations/${id}`, { method: 'PATCH', body: { title } }),
  clear: (id: string) => api<void>(`${v1}/conversations/${id}/clear`, { method: 'POST' }),
  leave: (id: string) => api<void>(`${v1}/conversations/${id}/leave`, { method: 'POST' }),
  remove: (id: string) => api<void>(`${v1}/conversations/${id}`, { method: 'DELETE' }),
  members: (id: string) => api<Member[]>(`${v1}/conversations/${id}/members`),
  addMembers: (id: string, userIds: string[]) =>
    api<void>(`${v1}/conversations/${id}/members`, { method: 'POST', body: { userIds } }),
  changeRole: (id: string, userId: string, role: 'Admin' | 'Member') =>
    api<void>(`${v1}/conversations/${id}/members/${userId}`, { method: 'PATCH', body: { role } }),
  removeMember: (id: string, userId: string) => api<void>(`${v1}/conversations/${id}/members/${userId}`, { method: 'DELETE' }),
  messages: (id: string, cursor: { before?: number; after?: number; limit?: number } = {}) =>
    api<MessagePage>(`${v1}/conversations/${id}/messages${q(cursor)}`),
  send: (id: string, input: SendInput) => api<Message>(`${v1}/conversations/${id}/messages`, { method: 'POST', body: input }),
  markRead: (id: string, upToSequence: number) =>
    api<{ lastReadSequence: number }>(`${v1}/conversations/${id}/read`, { method: 'POST', body: { upToSequence } }),
  statuses: (id: string, fromSequence: number) =>
    api<MessageStatusItem[]>(`${v1}/conversations/${id}/statuses${q({ fromSequence })}`),
  /** Starts a summary of the messages after `afterSequence` (default: after my read position). */
  startSummary: (id: string, afterSequence?: number) =>
    api<SummaryJob>(`${v1}/conversations/${id}/summary${q({ afterSequence })}`, { method: 'POST' }),
  summary: (id: string, jobId: string) => api<SummaryJob>(`${v1}/conversations/${id}/summary/${jobId}`),
}

export const aiApi = {
  config: () => api<AiConfig>(`${v1}/ai/config`),
}

export const messagesApi = {
  edit: (id: string, body: string) => api<Message>(`${v1}/messages/${id}`, { method: 'PATCH', body: { body } }),
  remove: (id: string) => api<void>(`${v1}/messages/${id}`, { method: 'DELETE' }),
  history: (id: string) => api<EditHistoryEntry[]>(`${v1}/messages/${id}/history`),
  info: (id: string) => api<MessageInfo>(`${v1}/messages/${id}/info`),
  search: (term: string, conversationId?: string, limit = 20, offset = 0) =>
    api<Paged<SearchHit>>(`${v1}/search/messages${q({ q: term, conversationId, limit, offset })}`),
}

export const pushApi = {
  config: () => api<{ enabled: boolean; publicKey: string | null }>(`${v1}/push/config`, { anonymous: true }),
  subscribe: (subscription: PushSubscriptionJSON) =>
    api<void>(`${v1}/push/subscriptions`, {
      method: 'POST',
      body: { endpoint: subscription.endpoint, keys: { p256dh: subscription.keys?.p256dh, auth: subscription.keys?.auth } },
    }),
  unsubscribe: (endpoint: string) => api<void>(`${v1}/push/subscriptions`, { method: 'DELETE', body: { endpoint } }),
}

export const adminApi = {
  users: (term: string, limit: number, offset: number) => api<Paged<AdminUser>>(`${v1}/admin/users${q({ q: term, limit, offset })}`),
  suspend: (id: string) => api<void>(`${v1}/admin/users/${id}/suspend`, { method: 'POST' }),
  unsuspend: (id: string) => api<void>(`${v1}/admin/users/${id}/unsuspend`, { method: 'POST' }),
  audit: (limit: number, offset: number) => api<Paged<AuditEntry>>(`${v1}/admin/audit${q({ limit, offset })}`),
}
