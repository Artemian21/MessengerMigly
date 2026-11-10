import { expect, test } from '@playwright/test'
import { login, registerAndVerify } from './helpers'

// Needs the AI summary switched on: the API with Ai__Enabled=true and the Python service (ai/) with AI_PROVIDER=fake and the
// same secret in Ai__SharedSecret / AI_SHARED_SECRET. The fake provider answers "- Fake summary of N message(s).".
test('unread messages are summarized from the opened chat', async ({ browser }) => {
  const stamp = Date.now().toString(36)
  const alice = await (await browser.newContext()).newPage()
  const bob = await (await browser.newContext()).newPage()
  const aliceName = `e2esa${stamp}`
  const bobName = `e2esb${stamp}`
  const aliceEmail = await registerAndVerify(alice, aliceName)
  const bobEmail = await registerAndVerify(bob, bobName)

  await login(alice, aliceEmail)
  await alice.getByRole('link', { name: '+ новий чат' }).click()
  await alice.getByPlaceholder(/@іменем/).fill(bobName)
  await alice.getByRole('button', { name: 'написати' }).first().click()
  for (const text of ['Дедлайн лаби у пʼятницю', 'Хто здає разом?']) {
    await alice.getByRole('textbox', { name: 'Повідомлення' }).fill(text)
    await alice.keyboard.press('Enter')
    await expect(alice.locator('.bubble-text', { hasText: text })).toBeVisible()
  }

  await login(bob, bobEmail)
  await bob.getByRole('link', { name: new RegExp(`E2E ${aliceName}`) }).click()

  // Opening the chat marks it read; the banner still knows what was unread.
  await expect(bob.getByText('2 непрочитані повідомлення')).toBeVisible()
  await bob.getByRole('button', { name: 'Підсумувати' }).click()
  await expect(bob.getByText('- Fake summary of 2 message(s).')).toBeVisible()
  await expect(bob.getByText(/Створено ШІ/)).toBeVisible()

  const summary = bob.getByText('- Fake summary of 2 message(s).')
  const lastMessage = bob.locator('.bubble-text', { hasText: 'Хто здає разом?' })

  // The summary stays until it is closed. First through leaving the chat and coming back right away (everything is read
  // now, so no new offer appears either: the per-conversation cache still says "2 unread" for 15 s, the list does not) ...
  await bob.getByRole('link', { name: `@${bobName}` }).click() // settings page
  await bob.getByRole('link', { name: new RegExp(`E2E ${aliceName}`) }).click()
  await expect(lastMessage).toBeVisible()
  await expect(summary).toBeVisible()
  await expect(bob.getByRole('button', { name: 'Підсумувати' })).toHaveCount(0)

  // ... then through a page reload (kept in sessionStorage) ...
  await bob.reload()
  await expect(lastMessage).toBeVisible()
  await expect(summary).toBeVisible()

  // ... and closing it is final: the card is gone, nothing new is offered, and a reload does not bring it back.
  await bob.getByRole('button', { name: 'Закрити' }).click()
  await expect(summary).toHaveCount(0)
  await expect(bob.getByRole('button', { name: 'Підсумувати' })).toHaveCount(0)
  await bob.reload()
  await expect(lastMessage).toBeVisible()
  await expect(summary).toHaveCount(0)
})
