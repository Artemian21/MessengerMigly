// Mirrors the server DTOs (camelCase JSON, ISO-8601 UTC timestamps).

export interface Me {
  id: string
  email: string
  username: string
  displayName: string
  role: 'User' | 'Admin'
  emailConfirmed: boolean
  needsProfile: boolean
  hasPassword: boolean
  createdAt: string
}

export interface AuthResponse {
  accessToken: string
  expiresAt: string
  user: Me
}

export interface UserSummary {
  id: string
  username: string
  displayName: string
}

export interface PublicUser extends UserSummary {
  isDeleted: boolean
  online: boolean | null
  lastSeenAt: string | null
}

export interface Sender {
  id: string
  username: string
  displayName: string
  isDeleted: boolean
}

export interface ReplyPreview {
  id: string
  sequence: number
  senderDisplayName: string
  bodyPreview: string | null
  isDeleted: boolean
}

export interface Attachment {
  id: string
  fileName: string
  sizeBytes: number
  contentType: string
}

export type MessageStatus = 'Sent' | 'Delivered' | 'Read'

export interface Message {
  id: string
  conversationId: string
  sequence: number
  updatedSequence: number
  sender: Sender
  body: string | null
  isDeleted: boolean
  isEdited: boolean
  replyTo: ReplyPreview | null
  clientMessageId: string
  createdAt: string
  editedAt: string | null
  deletedAt: string | null
  attachments: Attachment[]
  status: MessageStatus | null
}

/** Client-side view of a message: server data plus optimistic send state. */
export interface UiMessage extends Message {
  pending?: 'sending' | 'failed'
}

export interface MessagePreview {
  id: string
  sequence: number
  senderId: string
  senderDisplayName: string
  body: string | null
  isDeleted: boolean
  createdAt: string
}

export interface Conversation {
  id: string
  type: 'Direct' | 'Group'
  title: string | null
  peer: PublicUser | null
  myRole: 'Owner' | 'Admin' | 'Member'
  memberCount: number
  lastSequence: number
  lastReadSequence: number
  visibleFromSequence: number
  unreadCount: number
  lastMessage: MessagePreview | null
  lastMessageAt: string | null
  createdAt: string
  isDeleted: boolean
}

export interface Member {
  userId: string
  username: string
  displayName: string
  role: 'Owner' | 'Admin' | 'Member'
  joinedAt: string
  isDeleted: boolean
  online: boolean | null
  lastSeenAt: string | null
}

export interface MessagePage {
  items: Message[]
  hasMore: boolean
}

export interface MessageStatusItem {
  messageId: string
  sequence: number
  status: MessageStatus
}

export interface ReceiptInfo {
  userId: string
  username: string
  displayName: string
  deliveredAt: string | null
  readAt: string | null
}

export interface MessageInfo {
  messageId: string
  status: MessageStatus
  recipients: ReceiptInfo[]
}

export interface EditHistoryEntry {
  previousBody: string
  editedAt: string
}

export interface SearchHit {
  messageId: string
  conversationId: string
  conversationTitle: string | null
  sequence: number
  sender: Sender
  snippet: string
  createdAt: string
}

export interface Paged<T> {
  items: T[]
  total: number
  next: number | null
}

export interface Session {
  id: string
  createdAt: string
  lastUsedAt: string
  expiresAt: string
  userAgent: string | null
  ip: string | null
  current: boolean
}

export interface AdminUser {
  id: string
  email: string
  username: string
  displayName: string
  role: string
  status: 'Active' | 'Suspended' | 'Deleted'
  emailConfirmed: boolean
  createdAt: string
  lastSeenAt: string | null
}

export interface AuditEntry {
  id: string
  actorUserId: string | null
  action: string
  entityType: string
  entityId: string | null
  dataJson: string | null
  ip: string | null
  createdAt: string
}

export interface Presence {
  userId: string
  online: boolean
  lastSeenAt: string | null
}

export interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
}
