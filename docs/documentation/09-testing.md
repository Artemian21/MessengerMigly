# 9. Testing

| Suite | Location | Tests | Needs |
|---|---|---|---|
| Domain | `server/tests/Messenger.Domain.Tests` | 49 | nothing |
| Application | `server/tests/Messenger.Application.Tests` | 17 | nothing |
| Architecture | `server/tests/Messenger.Architecture.Tests` | 4 | nothing |
| Integration (+ SignalR, localization) | `server/tests/Messenger.Api.IntegrationTests` | 59 | Docker (Testcontainers PostgreSQL) |
| Client | `client/src/**/*.test.ts(x)` | 26 | nothing |
| End-to-end | `e2e/tests` | 1 | running stack + Mailpit |

Tools: xUnit, Shouldly (not FluentAssertions), NSubstitute, Testcontainers.PostgreSql, `Microsoft.AspNetCore.Mvc.Testing`, SignalR client, NetArchTest.Rules; Vitest + Testing Library + user-event; Playwright.

## Commands
```bash
dotnet test server/Messenger.sln                                   # all server suites
cd client && npm run lint && npm test -- --run && npm run build
cd e2e && npx playwright test                                      # start API (:5000), Vite (:5173), compose services first
```

## Domain tests
- `ConversationTests` — group size, roles and permissions, watermarks, ownership transfer, deleted groups.
- `MessageTests` — 48 h edit window, edit history, tombstone, authorship, status aggregation.
- `UserAndRulesTests` — input rules (NFC, grapheme counting, control characters), usernames, passwords, lockout, account deletion, refresh grace, single-use tokens.

## Application tests
- `BehaviorTests` — validation → 422 shape, anonymous and non-admin rejection, transaction wrapping (commands only), paging, `LIKE` escaping, search snippets.
- `AuthHandlerTests` — generic 401, unverified login, refresh rotation, reuse detection, grace-window behaviour, neutral registration.

## Architecture tests (`LayerTests`)
Domain has no references to Application/Infrastructure/Api/EF/ASP.NET/Npgsql/MediatR/FluentValidation; Application has none to Infrastructure/Api/ASP.NET/Npgsql/BCrypt/IdentityModel and references only Domain; handlers are sealed.

## Integration tests
`ApiFactory` starts `postgres:16-alpine` once per fixture, runs the app in the `Testing` environment (development JWT secret allowed, BCrypt work factor 4, relaxed rate limits, 1 s presence grace) and replaces `IEmailSender` with a capturing sender so tests can follow verification/reset links. `TestUsers` registers, verifies and logs in unique users and offers helpers (send, group creation, JSON GET).

| File | Covers |
|---|---|
| `AuthFlowTests` | register → verify → login → refresh rotation → logout, reuse detection, CSRF header, tampered/missing token, wrong password, lockout, password reset/change, sessions, 422 and malformed JSON |
| `MessagingTests` | direct-chat creation, self chat, validation, idempotent send, concurrent sends (unique monotonic sequences), concurrent first messages (one chat), cursor paging, edit/history/info rules and resync of edits, tombstone + reply to deleted, read/delivered status, clear chat, attachments |
| `GroupsSearchAndBolaTests` | group rules and roles, join watermark, owner leaving, deleted group, **BOLA suite** (outsider cannot read/send/edit/delete/search/ack/mark-read), search visibility (before join, after clear, deleted), wildcard escaping, user search |
| `AccountAdminAndOpsTests` | account deletion (frees username, ownership transfer, messages kept), username conflicts, admin role + suspension + audit, `/health`, `/docs`, OpenAPI, JSON 404 and security headers |
| `RateLimitTests` | 429 + `Retry-After` on the auth policy |
| `RealtimeTests` | send → receive → ack → status via two hub connections, typing, presence grace and contact-only visibility, REST resync after reconnect, anonymous hub rejected |
| `InputHardeningTests` | NUL characters in emails, search text and push endpoints are rejected with 422, never 5xx; message translated to Ukrainian |
| `LocalizationTests` | uk validation errors, uk/en/unsupported-language fallbacks, entity-aware 404, email language |

## Client tests
- `lib/messages.test.ts` — merge ordering, optimistic replacement, stale revision handling, status monotonicity, tombstones, edit window.
- `api/http.test.ts` — single shared refresh for parallel 401s, sign-out on rejected refresh, problem details parsing, `Accept-Language`.
- `components/MessageBubble.test.tsx` — plain-text rendering (no HTML injection), ticks, deleted placeholder, retry, author-only actions.
- `state/summaries.test.ts` — finished-job handling (stale / duplicate answers ignored), settled vs running results, error-code mapping.
- `components/SummaryBanner.test.tsx` — offer only above the minimum and when the feature is on, start from the remembered read position, plain-text rendering of hostile text, error messages and retry, empty answer, hide.
- `lib/format.test.ts`, `i18n/i18n.test.ts` — formatting, key parity between locales, plural forms, interpolation.
- `test/setup.ts` forces the Ukrainian locale before each test.

## End-to-end (`e2e/tests/first-message.spec.ts`)
Two users register in the Ukrainian UI, verify through the link fetched from the Mailpit API, one signs in, finds the other by username, sends a first message, reloads the page, and the message is still there. Configuration: `BASE_URL` (default `http://localhost:5173`), `MAILPIT_URL` (default `http://localhost:8025`).

### AI summary scenario (`e2e/tests/summary.spec.ts`)
Alice sends two messages, Bob opens the chat, sees "2 непрочитані повідомлення", presses "Підсумувати" and gets the fake provider's answer. It needs the Python service (`AI_PROVIDER=fake`) and the API with `Ai__Enabled=true` and the same secret on both sides (`appsettings.Development.local.json` + `ai/.env`). Start the API with `RateLimiting__AuthPerMinute=1000`: the specs register several users within a minute. Shared steps (register, verify, login) are in `e2e/tests/helpers.ts`.

## Python service tests
`cd ai && python -m pytest -q` — factory, adapters on stubbed SDK clients, prompts (injection guards), chunking, API (401 before 422, 502 / 504 mapping). No test calls a real provider. CI job `ai`.

## Conventions
- Test names describe behaviour (`Delete_is_a_tombstone_and_replies_survive`).
- Integration tests create their own users (unique names) so they can share a container and run in parallel.
- A feature is done when it has a unit test where the rule lives and an integration test through HTTP/hub; update `docs/TRACEABILITY.md`.
