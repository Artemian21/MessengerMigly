import { expect, request, test } from '@playwright/test'

const mailpit = process.env.MAILPIT_URL ?? 'http://localhost:8025'
const password = 'E2e-strong-pass-1'

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

async function registerAndVerify(page: import('@playwright/test').Page, username: string) {
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

test('register → verify → login → send → reload keeps the message', async ({ browser }) => {
  const stamp = Date.now().toString(36)
  const bobPage = await (await browser.newContext()).newPage()
  const bobName = `e2ebob${stamp}`
  await registerAndVerify(bobPage, bobName)

  const page = await (await browser.newContext()).newPage()
  const aliceEmail = await registerAndVerify(page, `e2ealice${stamp}`)

  await page.goto('/login')
  await page.getByLabel('Email').fill(aliceEmail)
  await page.getByLabel('Пароль').fill(password)
  await page.getByRole('button', { name: 'Увійти' }).click()
  await expect(page.getByRole('link', { name: '+ новий чат' })).toBeVisible()

  await page.getByRole('link', { name: '+ новий чат' }).click()
  await page.getByPlaceholder(/@іменем/).fill(bobName)
  await page.getByRole('button', { name: 'написати' }).first().click()

  const text = `Hey, are you ready for class? ${stamp}`
  await page.getByLabel('Повідомлення').fill(text)
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/\/chats\//)
  await expect(page.locator('.bubble-text', { hasText: text })).toBeVisible()

  await page.reload()
  await expect(page.locator('.bubble-text', { hasText: text })).toBeVisible()
})
