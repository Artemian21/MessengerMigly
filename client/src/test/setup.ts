import '@testing-library/jest-dom/vitest'
import { afterEach, beforeEach } from 'vitest'
import { cleanup } from '@testing-library/react'
import { useLocale } from '../i18n'

beforeEach(() => useLocale.setState({ locale: 'uk' }))
afterEach(() => cleanup())
