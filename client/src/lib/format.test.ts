import { describe, expect, it } from 'vitest'
import { formatBytes, formatDay, formatLastSeen, initials } from './format'

describe('format', () => {
  const now = new Date(2026, 9, 10, 15, 0, 0)

  it('labels today and yesterday', () => {
    expect(formatDay(new Date(2026, 9, 10, 9, 0).toISOString(), now)).toBe('Сьогодні')
    expect(formatDay(new Date(2026, 9, 9, 23, 0).toISOString(), now)).toBe('Вчора')
  })

  it('describes presence', () => {
    expect(formatLastSeen(true, null, now)).toBe('у мережі')
    expect(formatLastSeen(false, new Date(now.getTime() - 5 * 60_000).toISOString(), now)).toBe('був(ла) 5 хв тому')
    expect(formatLastSeen(false, null, now)).toBe('не в мережі')
  })

  it('formats sizes and initials', () => {
    expect(formatBytes(512)).toBe('512 Б')
    expect(formatBytes(2048)).toBe('2.0 КБ')
    expect(initials('Alice Kovalenko')).toBe('AK')
    expect(initials('bob')).toBe('BO')
  })
})
