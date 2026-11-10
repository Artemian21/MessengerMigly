import { useCallback } from 'react'
import { create } from 'zustand'
import uk from './locales/uk.json'
import en from './locales/en.json'

/** UI languages. Strings live in ./locales/*.json; uk.json is the source of truth for keys. */
export const LOCALES = ['uk', 'en'] as const
export type Locale = (typeof LOCALES)[number]

const resources: Record<Locale, Record<string, string>> = { uk, en }
const STORAGE_KEY = 'migly.lang'
const PLURAL_SUFFIX = /_(zero|one|two|few|many|other)$/

type Keys = keyof typeof uk
type PluralBase<K> = K extends `${infer Base}_other` ? Base : never
export type TKey = Exclude<Keys, `${string}_${'one' | 'few' | 'many' | 'other'}`> | PluralBase<Keys>
export type TParams = Record<string, string | number>

/** BCP 47 tag for Intl date/number formatting. */
export const localeTag = (locale: Locale) => (locale === 'uk' ? 'uk-UA' : 'en-GB')

function detectLocale(): Locale {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'uk' || saved === 'en') return saved
  } catch {
    // Storage may be unavailable (private mode); fall back to the browser language.
  }
  const browser = typeof navigator !== 'undefined' ? navigator.language.toLowerCase() : 'uk'
  return browser.startsWith('en') ? 'en' : 'uk'
}

interface LocaleState {
  locale: Locale
  setLocale: (locale: Locale) => void
}

export const useLocale = create<LocaleState>((set) => ({
  locale: detectLocale(),
  setLocale: (locale) => {
    try {
      localStorage.setItem(STORAGE_KEY, locale)
    } catch {
      // Not persisted; the choice still applies to this tab.
    }
    document.documentElement.lang = locale
    set({ locale })
  },
}))

export function translate(locale: Locale, key: TKey, params?: TParams): string {
  const dictionary = resources[locale]
  let text: string | undefined
  if (params && typeof params.count === 'number') {
    const rule = new Intl.PluralRules(localeTag(locale)).select(params.count)
    text = dictionary[`${key}_${rule}`] ?? dictionary[`${key}_other`]
  }
  text ??= dictionary[key] ?? resources.uk[key] ?? key
  return params ? text.replace(/\{(\w+)\}/g, (match, name: string) => (name in params ? String(params[name]) : match)) : text
}

/** Non-hook translation for code outside React (formatters, confirm dialogs). */
export const t = (key: TKey, params?: TParams) => translate(useLocale.getState().locale, key, params)

export function useT() {
  const locale = useLocale((s) => s.locale)
  return useCallback((key: TKey, params?: TParams) => translate(locale, key, params), [locale])
}

export function currentLocaleTag(): string {
  return localeTag(useLocale.getState().locale)
}

export function resourceKeys(locale: Locale): string[] {
  return Object.keys(resources[locale])
}

/** Keys without plural suffixes: languages differ in plural forms (uk: one/few/many/other, en: one/other). */
export function baseKeys(locale: Locale): string[] {
  return [...new Set(resourceKeys(locale).map((key) => key.replace(PLURAL_SUFFIX, '')))]
}
