# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Migly is a real-time messenger (ASP.NET Core 9 + PostgreSQL + SignalR backend, React 19/Vite/TypeScript client, Playwright e2e). `AGENTS.md` holds the binding project rules (layering, UTC via `IClock`, BOLA request interfaces, i18n parity, `ASSUMPTIONS.md` updates, no secrets/message bodies in logs) — read it first; this file does not repeat them. Full module docs live in `docs/documentation/` (01–10); `README.md` has the curl examples and data-model overview.

## Commands

Prerequisites: Docker, .NET 9 SDK, Node 22.

```bash
docker compose up -d postgres mailpit                                   # Postgres :5432, Mailpit UI :8025
dotnet run --project server/src/Messenger.Api --launch-profile http     # API on :5000 (applies migrations at startup)
dotnet run --project server/src/Messenger.Api --launch-profile http -- seed   # demo data (alice@migly.local, admin@migly.local)
cd client && npm install && npm run dev                                 # Vite on :5173
```

```bash
dotnet build server/Messenger.sln -c Release
dotnet test server/Messenger.sln                                        # integration tests need Docker (Testcontainers)
dotnet test server/tests/Messenger.Domain.Tests                         # one project
dotnet test server/Messenger.sln --filter "FullyQualifiedName~SendMessage"   # one test/class by name
cd client && npm run lint && npm test -- --run && npm run build         # lint is --max-warnings 0
cd client && npx vitest run src/lib/messages.test.ts                    # one client test file
cd e2e && npx playwright test                                           # needs API :5000 + Vite :5173 running (or BASE_URL)
scripts/smoke.sh <base-url>                                             # register → verify via Mailpit → login → 401
```

CI (`.github/workflows/ci.yml`) runs server build+test, client lint/test/build, docker build, and gitleaks.

## Architecture

Four server projects under `server/src`, with test projects mirroring them in `server/tests` (Domain, Application, Api.IntegrationTests, and `Messenger.Architecture.Tests` which enforces the layer dependency rules via NetArchTest):

- **Domain** — entities, invariants, domain events, `Result`/`Error`; no framework dependencies.
- **Application** — MediatR commands/queries, validators, and ports (`Abstractions/Ports.cs`). Handlers are grouped by feature folder (Auth, Conversations, Messages, Users, Push, Admin, Realtime).
- **Infrastructure** — EF Core/Npgsql persistence, repositories, unit of work, JWT/BCrypt, email, web push, demo seeder, migrations.
- **Api** — controllers under `/api/v1`, `ChatHub`, presence, ProblemDetails mapping, rate limiting, OpenAPI/Scalar (`/docs`). Composition root. In production it also serves the built SPA from `wwwroot`.

### Request pipeline
Every MediatR request flows through behaviors in `Application/Common/Behaviors.cs`, in order: Logging → Validation (FluentValidation → 422 with per-field errors) → Authorization (auth, system role, BOLA membership; inaccessible conversations/messages return 404 to avoid leaking existence) → Transaction. Handlers return `Result<T>`; the API layer translates `Error` types to ProblemDetails (RFC 9457) and localizes messages at the boundary (`ApiLocalizer` + `ApiMessages*.resx`), which is why Domain/Application messages are English.

### Messages and ordering
Sending is a single DB transaction: the conversation's `last_sequence` is incremented with `UPDATE … RETURNING` (row lock serializes senders), then the message is inserted with that sequence. Ordering is **per-conversation sequence**, never timestamps or a global id. Edits/deletes take a new `UpdatedSequence` from the same counter so clients resync with `after=N`. Deletes are tombstones. `clientMessageId` makes sends idempotent (unique index). Per-member sequence watermarks (joined / cleared / read) are applied by every read path (history, search, unread, status) — new read paths must apply them too.

### Domain events and realtime
Entities collect domain events; `DomainEventInterceptor` buffers them on SaveChanges and `DomainEventDispatcher` publishes them only **after commit** (`UnitOfWork`), swallowing handler failures. Post-commit handlers in `Application/Realtime` call `IRealtimeNotifier` (SignalR impl in Api). Principle: **REST changes state, the hub only carries events and ephemeral signals** (`AckDelivered`, `MarkRead`, `Typing`, `OpenConversation`, `WatchPresence`). Messages/status events are routed per user (`Clients.User`, JWT `sub`), groups are only a routing cache for typing/presence. Hub methods go through MediatR so the same authorization behaviors apply. Presence and groups are in memory → single-instance only. Details: `docs/documentation/06-realtime.md`.

### Auth
Own auth: BCrypt passwords, short-lived HS256 JWT access token + HttpOnly refresh cookie with rotation and reuse detection, email verification and password reset via Mailpit/Brevo, optional Google/GitHub OAuth, lockout, rate limiting. Config is env-driven (`Section__Key`); `docs/documentation/10-operations.md` lists every variable and `.env.example` mirrors them.

### Client (`client/src`)
`api/` (HTTP + refresh handling, endpoint wrappers, DTO types), `realtime/hub.ts` (SignalR), `state/` (Zustand: auth, connectivity), TanStack Query for server data (`hooks/useMessages.ts`), `pages/` + `components/`, own i18n in `i18n/` (flat JSON keys, `{param}` placeholders, `_one/_few/_many/_other` plurals), theme tokens in `styles/theme.css`. Dev proxying to the API is configured in `vite.config.ts`.

## Misc

- Docs worth keeping in sync with changes: `docs/ASSUMPTIONS.md` (required by AGENTS.md), `docs/DECISIONS.md`, `docs/TRACEABILITY.md`, and the relevant `docs/documentation/*.md` module file.
- `README.md` links to some docs that don't exist yet (`docs/DEPLOYMENT.md`, `HUMAN_TODO.md`, `DEPLOY_STEP_BY_STEP.md`, `OWNERSHIP.md`); the app isn't deployed yet (Render + Neon blueprint in `render.yaml`).
