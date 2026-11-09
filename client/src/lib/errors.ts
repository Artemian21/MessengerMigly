import { ApiError } from '../api/http'
import { t } from '../i18n'

export function errorMessage(error: unknown): string {
  return error instanceof ApiError ? error.message : t('common.error')
}

export function fieldError(error: unknown, field: string): string | undefined {
  return error instanceof ApiError ? error.field(field) : undefined
}
