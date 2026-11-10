# Progress

Ticked = implemented **and** verified (automated test or a recorded manual check).

- [x] Phase 0: walking skeleton — layered solution, controllers, MediatR pipeline, ProblemDetails/422, Serilog JSON, `/health`, `/health/ready`, Scalar `/docs`, ForwardedHeaders, docker-compose, Dockerfile (built and run locally), CI, migration + advisory lock
- [x] Phase 1: domain + persistence — entities/invariants, EF configs, partial unique indexes, DirectKey, repositories + unit of work, post-commit domain events, `IClock`, seed command
- [x] Phase 2: auth — register, verify, resend, login, lockout, JWT, refresh rotation + reuse detection + grace, logout, `/users/me`, auth rate limit
- [x] Phase 3: conversations & messages — direct get-or-create, idempotent send with per-conversation sequence, history, replies, edit + history, tombstones, groups/members/roles, watermarks on every read path, unread, search, statuses/sync
- [x] Phase 4: realtime — ChatHub, JWT over WebSocket, events, ack/read receipts, aggregate status, message info, typing, presence with grace, resync
- [x] Phase 5: React client — auth screens, chat list, chat window with virtualised infinite scroll, optimistic sending with retry, groups, search, message info/history, settings/sessions, admin, waking-up banner, PWA
- [x] Phase 6: account lifecycle — forgot/reset, change password, username change, delete account with ownership transfer, sessions, system admin + audit log; OAuth implemented (needs credentials to verify)
- [x] Phase 7: Web Push + PWA — implemented (VAPID sender, subscriptions, service worker); live push needs VAPID keys to verify
- [x] Phase 8: hardening — rate limits, security headers/CSP, limit/offset+total, attachment metadata, architecture tests
- [x] Localization (uk/en UI, en/uk API + emails) and Neumorphism theme
- [x] Phase 9: delivery — theme, README, docs, request collection, realistic seed, traceability, ownership, human TODO

## Verification run (2026-11-03)

| Command | Result |
|---|---|
| `dotnet build server/Messenger.sln -c Release` | 0 errors, 0 warnings |
| `dotnet test server/Messenger.sln` | 129 passed (Domain 49, Application 17, Architecture 4, Integration incl. SignalR, localization and input hardening 59) |
| `cd client && npm run lint && npm test -- --run && npm run build` | lint clean, 25 passed, build ok |
| `docker build -t messenger .` + `docker run` against compose Postgres | SPA, `/docs`, `/health`, `/health/ready` OK |
| `sh scripts/smoke.sh http://localhost:5000` | passed (register → Mailpit verify → login → me → 401) |
| `cd e2e && npx playwright test` | 1 passed |

## Not verifiable without human accounts

OAuth sign-in (Google/GitHub), live Web Push delivery, Brevo email, public deployment. See `HUMAN_TODO.md`.

## Blocked

None.
