import { describe, expect, it } from 'vitest'
import { baseKeys, LOCALES, resourceKeys, translate } from './index'

describe('i18n resources', () => {
  it('uk and en define the same keys (plural forms may differ)', () => {
    expect([...baseKeys('en')].sort()).toEqual([...baseKeys('uk')].sort())
  })

  it('every plural key has an _other form in each language', () => {
    for (const locale of LOCALES) {
      const keys = resourceKeys(locale)
      for (const key of keys.filter((k) => /_(one|few|many)$/.test(k))) {
        expect(keys, `${locale}:${key}`).toContain(key.replace(/_(one|few|many)$/, '_other'))
      }
    }
  })

  it('has no empty translations', () => {
    for (const locale of LOCALES) {
      for (const key of resourceKeys(locale)) {
        expect(translate(locale, key as never).trim(), `${locale}:${key}`).not.toBe('')
      }
    }
  })

  it('uses Ukrainian plural forms and English singular/plural', () => {
    expect(translate('uk', 'chat.memberCount', { count: 1 })).toBe('1 учасник')
    expect(translate('uk', 'chat.memberCount', { count: 3 })).toBe('3 учасники')
    expect(translate('uk', 'chat.memberCount', { count: 5 })).toBe('5 учасників')
    expect(translate('en', 'chat.memberCount', { count: 1 })).toBe('1 member')
    expect(translate('en', 'chat.memberCount', { count: 21 })).toBe('21 members')
  })

  it('interpolates parameters', () => {
    expect(translate('en', 'auth.continueWith', { provider: 'Google' })).toBe('Continue with Google')
  })
})
