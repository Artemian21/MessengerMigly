import { expect, request, type Page } from '@playwright/test'

const mailpit = process.env.MAILPIT_URL ?? 'http://localhost:8025'
export const password = 'E2e-strong-pass-1'

async function verificationLink(email: string): Promise<string> {
  const api = await request.newContext()
  for (let attempt = 0; attempt < 20; attempt++) {
    const search = await api.get(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`)
    const { messages } = (await search.json()) as { messages: { ID: string }[] }
    if (messages.length > 0) {
      const message = (await (await api.get(`${mailpit}/api/v1/message/${messages[0].ID}`)).json()) as { Text: string }
      const match = message.Text.match(/token=([A-Za-z0-9_-]+)/)
      if (match) return `/verify-email?token=${match[1]}`
    }
    await new Promise((resolve) => setTimeout(resolve, 500))
  }
  throw new Error(`No verification email for ${email}`)
}

/** Registers `username` through the UI and confirms the email via Mailpit. Returns the email address. */
export async function registerAndVerify(page: Page, username: string): Promise<string> {
  const email = `${username}@example.com`
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel(/Ім'я користувача/).fill(username)
  await page.getByLabel("Відображуване ім'я").fill(`E2E ${username}`)
  await page.getByLabel(/Пароль/).fill(password)
  await page.getByRole('button', { name: 'Створити акаунт' }).click()
  await expect(page.getByText(/посилання для підтвердження/)).toBeVisible()

  await page.goto(await verificationLink(email))
  await expect(page.getByText('Email підтверджено')).toBeVisible()
  return email
}

export async function login(page: Page, email: string) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Пароль').fill(password)
  await page.getByRole('button', { name: 'Увійти' }).click()
  await expect(page.getByRole('link', { name: '+ новий чат' })).toBeVisible()
}
