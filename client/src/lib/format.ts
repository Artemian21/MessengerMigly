import { currentLocaleTag, t } from '../i18n'

// Timestamps arrive in UTC (ISO-8601) and are shown in the viewer's local time zone and UI language (NFR-12).

export function formatTime(iso: string, locale = currentLocaleTag()): string {
  return new Date(iso).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
}

function daysAgo(iso: string, now: Date): number {
  const date = new Date(iso)
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  const startOfDate = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime()
  return Math.round((startOfToday - startOfDate) / 86_400_000)
}

export function formatDay(iso: string, now = new Date(), locale = currentLocaleTag()): string {
  const days = daysAgo(iso, now)
  if (days === 0) return t('time.today')
  if (days === 1) return t('time.yesterday')
  const date = new Date(iso)
  return date.toLocaleDateString(locale, { day: 'numeric', month: 'short', year: date.getFullYear() === now.getFullYear() ? undefined : 'numeric' })
}

export function formatListTime(iso: string, now = new Date()): string {
  return daysAgo(iso, now) === 0 ? formatTime(iso) : formatDay(iso, now)
}

export function formatLastSeen(online: boolean | null | undefined, lastSeenAt: string | null | undefined, now = new Date()): string {
  if (online) return t('time.online')
  if (!lastSeenAt) return t('time.offline')
  const minutes = Math.floor((now.getTime() - Date.parse(lastSeenAt)) / 60_000)
  if (minutes < 1) return t('time.justNow')
  if (minutes < 60) return t('time.minutesAgo', { count: minutes })
  const days = daysAgo(lastSeenAt, now)
  const time = formatTime(lastSeenAt)
  if (days === 0) return t('time.lastSeenToday', { time })
  if (days === 1) return t('time.lastSeenYesterday', { time })
  return t('time.lastSeenOn', { day: formatDay(lastSeenAt, now), time })
}

export function formatBytes(size: number): string {
  if (size < 1024) return t('size.b', { n: size })
  if (size < 1024 * 1024) return t('size.kb', { n: (size / 1024).toFixed(1) })
  return t('size.mb', { n: (size / 1024 / 1024).toFixed(1) })
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  return (parts.length > 1 ? parts[0][0] + parts[1][0] : name.slice(0, 2)).toUpperCase()
}
