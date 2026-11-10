import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { QueryClient } from '@tanstack/react-query'
import { currentAccessToken } from '../api/http'
import { conversationsApi } from '../api/endpoints'
import type { Conversation, Message, MessageStatus, Presence, SummaryJobStatus } from '../api/types'
import { useAuth } from '../state/auth'
import { useConnectivity } from '../state/connectivity'
import { useLive } from '../state/live'
import { useSummaries } from '../state/summaries'
import {
  applyDeletion,
  applyStatus,
  conversationsKey,
  DELETED_PLACEHOLDER,
  maxUpdatedSequence,
  mergeMessages,
  messagesKey,
  type MessagesCache,
} from '../lib/messages'

/**
 * SignalR client (D-06): events update the TanStack Query cache; state changes go through REST.
 * After a reconnect the client resyncs over REST (STS-08): conversations snapshot, then
 * messages changed after the last known sequence for open chats, then acknowledges delivery.
 */
class RealtimeClient {
  private connection: HubConnection | null = null
  private queryClient: QueryClient | null = null
  private openConversations = new Set<string>()
  private watched = new Set<string>()

  async start(queryClient: QueryClient) {
    if (this.connection) return
    this.queryClient = queryClient
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/chat', { accessTokenFactory: currentAccessToken })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, 1000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()
    this.connection = connection
    this.register(connection)

    const connectivity = useConnectivity.getState()
    connection.onreconnecting(() => connectivity.setHub('reconnecting'))
    connection.onreconnected(() => {
      connectivity.setHub('connected')
      void this.resync()
    })
    connection.onclose(() => {
      connectivity.setHub('disconnected')
      // Closed for good (e.g. token expiry closed the socket): start again with a fresh token.
      if (this.connection === connection && useAuth.getState().status === 'authenticated') {
        setTimeout(() => void this.connectWithRetry(connection), 2000)
      }
    })
    await this.connectWithRetry(connection)
  }

  async stop() {
    const connection = this.connection
    this.connection = null
    this.openConversations.clear()
    this.watched.clear()
    useConnectivity.getState().setHub('disconnected')
    if (connection) await connection.stop()
  }

  private async connectWithRetry(connection: HubConnection) {
    useConnectivity.getState().setHub('connecting')
    for (let attempt = 0; this.connection === connection; attempt++) {
      if (connection.state !== HubConnectionState.Disconnected) return
      try {
        await connection.start()
        useConnectivity.getState().setHub('connected')
        await this.resync()
        return
      } catch {
        useConnectivity.getState().setWaking(attempt > 0)
        await new Promise((resolve) => setTimeout(resolve, Math.min(30_000, 2000 * (attempt + 1))))
      }
    }
  }

  private get connected() {
    return this.connection?.state === HubConnectionState.Connected
  }

  async openConversation(id: string) {
    this.openConversations.add(id)
    if (this.connected) await this.connection!.invoke('OpenConversation', id).catch(() => undefined)
  }

  async closeConversation(id: string) {
    this.openConversations.delete(id)
    if (this.connected) await this.connection!.invoke('CloseConversation', id).catch(() => undefined)
  }

  typing(conversationId: string) {
    if (this.connected) void this.connection!.invoke('Typing', conversationId).catch(() => undefined)
  }

  async markRead(conversationId: string, upToSequence: number) {
    if (this.connected) {
      await this.connection!.invoke('MarkRead', conversationId, upToSequence).catch(() => undefined)
    } else {
      await conversationsApi.markRead(conversationId, upToSequence).catch(() => undefined)
    }
  }

  ack(messageIds: string[]) {
    if (messageIds.length > 0 && this.connected) {
      void this.connection!.invoke('AckDelivered', messageIds).catch(() => undefined)
    }
  }

  async watchPresence(userIds: string[]) {
    const fresh = userIds.filter((id) => !this.watched.has(id))
    if (fresh.length === 0 || !this.connected) return
    fresh.forEach((id) => this.watched.add(id))
    const snapshot = await this.connection!.invoke<Presence[]>('WatchPresence', fresh).catch(() => [] as Presence[])
    useLive.getState().setPresence(snapshot)
  }

  private register(connection: HubConnection) {
    connection.on('MessageReceived', (message: Message) => this.onMessage(message, true))
    connection.on('MessageUpdated', (message: Message) => this.onMessage(message, false))
    connection.on('MessageDeleted', (e: { conversationId: string; messageId: string; updatedSequence: number }) => {
      this.updateMessages(e.conversationId, (items) => applyDeletion(items, e.messageId, e.updatedSequence))
      this.updateConversation(e.conversationId, (c) =>
        c.lastMessage?.id === e.messageId ? { ...c, lastMessage: { ...c.lastMessage, body: DELETED_PLACEHOLDER, isDeleted: true } } : c,
      )
    })
    connection.on('MessageStatusChanged', (e: { conversationId: string; messageId: string; status: MessageStatus }) => {
      this.updateMessages(e.conversationId, (items) => applyStatus(items, e.messageId, e.status))
    })
    connection.on('ConversationUpdated', (e: { conversationId: string }) => {
      void this.queryClient?.invalidateQueries({ queryKey: conversationsKey })
      void this.queryClient?.invalidateQueries({ queryKey: ['conversation', e.conversationId] })
      void this.queryClient?.invalidateQueries({ queryKey: ['members', e.conversationId] })
    })
    // Handlers must not return a value: SignalR would treat it as a client result.
    connection.on('ConversationRead', (e: { conversationId: string; lastReadSequence: number }) => {
      this.updateConversation(e.conversationId, (c) => ({ ...c, unreadCount: 0, lastReadSequence: Math.max(c.lastReadSequence, e.lastReadSequence) }))
    })
    connection.on('Typing', (e: { conversationId: string; userId: string }) => {
      if (e.userId !== useAuth.getState().user?.id) useLive.getState().typingStarted(e.conversationId, e.userId)
    })
    connection.on('PresenceChanged', (p: Presence) => {
      useLive.getState().setPresence([p])
    })
    // The result of "summarize what I missed". GET conversations/{id}/summary/{jobId} is the fallback (see useSummary).
    connection.on('SummaryCompleted', (e: { conversationId: string; jobId: string; status: SummaryJobStatus; summary?: string | null; errorCode?: string | null }) => {
      useSummaries.getState().finished(e.conversationId, e)
    })
  }

  private onMessage(message: Message, isNew: boolean) {
    const me = useAuth.getState().user?.id
    this.updateMessages(message.conversationId, (items) => mergeMessages(items, [message]))
    if (isNew) {
      useLive.getState().typingStopped(message.conversationId, message.sender.id)
      if (message.sender.id !== me) this.ack([message.id])
      const known = this.updateConversation(message.conversationId, (c) => ({
        ...c,
        lastSequence: Math.max(c.lastSequence, message.sequence),
        lastMessageAt: message.createdAt,
        unreadCount: message.sender.id === me ? c.unreadCount : c.unreadCount + 1,
        lastMessage: {
          id: message.id,
          sequence: message.sequence,
          senderId: message.sender.id,
          senderDisplayName: message.sender.displayName,
          body: message.body,
          isDeleted: message.isDeleted,
          createdAt: message.createdAt,
        },
      }))
      if (!known) void this.queryClient?.invalidateQueries({ queryKey: conversationsKey })
    }
  }

  private updateMessages(conversationId: string, update: (items: MessagesCache['items']) => MessagesCache['items']) {
    this.queryClient?.setQueryData<MessagesCache>(messagesKey(conversationId), (cache) =>
      cache ? { ...cache, items: update(cache.items) } : cache,
    )
  }

  /** Returns false when the conversation is not in the cached list (e.g. a brand-new chat). */
  private updateConversation(conversationId: string, update: (c: Conversation) => Conversation): boolean {
    let found = false
    this.queryClient?.setQueryData<Conversation[]>(conversationsKey, (list) => {
      if (!list) return list
      const next = list.map((c) => {
        if (c.id !== conversationId) return c
        found = true
        return update(c)
      })
      return next.sort((a, b) => Date.parse(b.lastMessageAt ?? b.createdAt) - Date.parse(a.lastMessageAt ?? a.createdAt))
    })
    return found
  }

  private async resync() {
    const qc = this.queryClient
    if (!qc) return
    await qc.invalidateQueries({ queryKey: conversationsKey })
    const watched = [...this.watched]
    this.watched.clear()
    await this.watchPresence(watched)
    for (const id of this.openConversations) {
      await this.connection?.invoke('OpenConversation', id).catch(() => undefined)
      const cache = qc.getQueryData<MessagesCache>(messagesKey(id))
      if (!cache) continue
      let after = maxUpdatedSequence(cache.items)
      for (let page = 0; page < 10; page++) {
        const result = await conversationsApi.messages(id, { after, limit: 50 }).catch(() => null)
        if (!result || result.items.length === 0) break
        this.updateMessages(id, (items) => mergeMessages(items, result.items))
        this.ack(result.items.filter((m) => m.sender.id !== useAuth.getState().user?.id).map((m) => m.id))
        after = Math.max(...result.items.map((m) => m.updatedSequence))
        if (!result.hasMore) break
      }
      const statuses = await conversationsApi.statuses(id, 0).catch(() => [])
      this.updateMessages(id, (items) => statuses.reduce((acc, s) => applyStatus(acc, s.messageId, s.status), items))
    }
  }
}

export const realtime = new RealtimeClient()
