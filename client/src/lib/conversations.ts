import type { Conversation } from '../api/types'
import { t } from '../i18n'

export function conversationTitle(c: Conversation): string {
  if (c.type === 'Group') return c.title ?? t('list.group')
  return c.peer?.displayName ?? t('list.personal')
}
