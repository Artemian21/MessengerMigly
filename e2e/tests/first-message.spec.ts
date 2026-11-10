import { expect, test } from '@playwright/test'
import { login, registerAndVerify } from './helpers'

test('register → verify → login → send → reload keeps the message', async ({ browser }) => {
  const stamp = Date.now().toString(36)
  const bobPage = await (await browser.newContext()).newPage()
  const bobName = `e2ebob${stamp}`
  await registerAndVerify(bobPage, bobName)

  const page = await (await browser.newContext()).newPage()
  const aliceEmail = await registerAndVerify(page, `e2ealice${stamp}`)

  await login(page, aliceEmail)

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
