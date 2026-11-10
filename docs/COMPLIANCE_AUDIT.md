# Compliance audit — implementation vs. `01-requirements-analysis` and `02-architecture-and-plan`

Audit date: 2026-11-05. Method: every MUST/SHOULD requirement and every architecture decision was checked against the code, the automated tests (`docs/TRACEABILITY.md`) and — where tests did not prove it — against the running system (malformed-request fuzzing, database-outage simulation, 60 concurrent WebSockets, latency sampling, OpenAPI inspection).

## Verdict

| Area | Result |
|---|---|
| MUST requirements (doc 01) | all implemented; 1 defect found and fixed during this audit (see below) |
| SHOULD requirements | all implemented; OAuth and Web Push are implemented but **not covered by automated tests** |
| COULD requirements | implemented: sessions list, audit log, attachment metadata, group rename |
| Architecture decisions D-01…D-19 (doc 02) | all followed; 2 documented substitutions (.NET 9, `LIKE lower()`) |
| API contract (doc 02 §7–8) | complete; 1 route renamed (OAuth callback) |
| Items that need a human | public deployment, CD, live URL, media, third-party accounts (`docs/HUMAN_TODO.md`) |

## Defect found and fixed

**NUL (`\u0000`) in text input produced 503 instead of 422** (violates API-03 "no 5xx on garbage"). PostgreSQL cannot store NUL; the resulting `PostgresException` (SQLSTATE 22021) was mapped to "service unavailable". Affected: `login`, `register`, `forgot-password`, `resend-verification` (email), message/user/admin search (`q`), push unsubscribe.

Fix: control characters are rejected by validators (email rule, search validators, push validators) and, as defence in depth, `GlobalExceptionHandler` maps SQLSTATE class 22 to 422 while real outages stay 503. Regression tests: `InputHardeningTests` (8) and `Email_must_be_well_formed_and_free_of_control_characters` (5 cases).

## Live checks performed

| Check | Requirement | Result |
|---|---|---|
| 24 malformed/hostile requests (bad GUIDs, `limit=abc`, `before=-5`, `before`+`after`, array/null/empty body, wrong content type, 300 KB email, 5 MB message, zero GUID, invalid roles, `long.MaxValue` sequence, null lists) | API-03 | all 4xx (404/415/422/403/401/200 as appropriate), none 5xx after the fix |
| Database unreachable (second instance with a dead connection string) | Lab 01 Standard "storage failure", doc 02 §10 | `/health` 200, `/health/ready` 503, API calls 503 + `Retry-After`, no host/port/stack in the body, Ukrainian text with `Accept-Language: uk` |
| 60 concurrent WebSocket connections, one message sent | NFR-06 (≥ 50) | 60/60 connected in 350 ms; the message reached all 60 connections; send took 39 ms |
| Warm read latency, 40 calls each (dev machine) | NFR-05 | history page p95 11 ms; conversation list p95 21 ms; search p95 7 ms (targets: history < 200 ms) |
| OpenAPI document | API-04 | 52 operations, all with a summary; Bearer scheme; 401 documented on all 39 secured operations; examples on all 20 request schemas |
| Log inspection | API-11, NFR-10 | no tokens, no message bodies, no hub query string |

## Requirement-by-requirement notes

Full requirement → code → test mapping is in `docs/TRACEABILITY.md`. This section lists only what the audit found beyond "implemented and tested".

### Deviations from the documents (all recorded in `docs/ASSUMPTIONS.md` / `DECISIONS.md` unless marked new)

| # | Spec | Implementation | Assessment |
|---|---|---|---|
| 1 | .NET 10 LTS (§1 D-01) | .NET 9 (installed SDK) | allowed by doc 03/04; recorded |
| 2 | `ILIKE` + `pg_trgm` (D-14) | `LIKE` on `lower(body)` with a trigram index on `lower(body)` | same behaviour, keeps Application provider-agnostic; recorded |
| 3 | Domain events §4.2: `EmailVerified`, `PasswordChanged`, `PasswordReset`, `ConversationCreated`, `ParticipantAdded`, `ParticipantRemoved` | merged into `ConversationChanged`; email events are `VerificationEmailRequested` / `PasswordResetRequested`; no events for password/email changes (audit log records them instead) | functionally equivalent for the consumers that exist (realtime, push, email); **new, not previously recorded** |
| 4 | OAuth route `GET /auth/external/{provider}/callback` (§6.3, §8) | provider callbacks `/signin-google` and `/signin-github` (ASP.NET defaults, as in §12) then `GET /auth/external/complete`; extra `GET /auth/external/providers` for the login page | cosmetic; **new** |
| 5 | `IEmailSender` "Console/File in dev" (§10, doc 04 stubs table) | Mailpit SMTP (default), Brevo, and `None` (drops mail, logs only the subject); no Console/File sender | Mailpit covers the dev case; **new** |
| 6 | AUD-01: audit "role changes" | audited: register, login, failed login, refresh reuse, password change/reset, account deletion, suspend/unsuspend, OAuth link. **Not audited:** group role changes and the `promote` CLI | gap in a COULD item; **new** |

### Gaps in verification

| # | Item | State |
|---|---|---|
| 1 | OAuth (AUTH-09) and Web Push (NTF-01) | implemented and wired, but no automated tests (doc 04 asks for a fake OAuth handler / fake `IPushSender` tests). Cannot be exercised for real without provider credentials / VAPID keys |
| 2 | NFR-05 for sends | single send measured (39 ms incl. fan-out to 60 sockets); p95 under load not measured because the send rate limit (burst 10, 30/min) intentionally throttles a load script |
| 3 | Kill/restart persistence (§9.1) | covered indirectly (all state is in PostgreSQL; E2E reloads the page); no explicit "kill the container" run |
| 4 | Code coverage (doc 04 Phase 8) | coverage is collected in CI but no report or threshold is published |
| 5 | Docker image | rebuilt on 2026-11-05 after the localization, theme, rename and NUL fix (`messenger:release`): SPA, `/docs`, `/health`, `/health/ready`, manifest and service worker served, `scripts/smoke.sh` passed, NUL regression returns 422, no errors in the container log. Rebuild after any later change |
| 6 | Playwright in CI | E2E runs locally only (1 scenario, within the 1–2 required); CI runs server, client, image build and gitleaks |

### Needs a human (cannot be completed from the repository)

Live URL and `/docs` on the public host; Render auto-deploy after CI (OPS-02 CD); Neon/Brevo/OAuth/VAPID accounts and secrets; gitleaks run in CI on real history (the repository has no commits yet); screenshot and 15-second recording; filling README placeholders; uptime monitor before a demo. Order and commands: `docs/HUMAN_TODO.md`.

## Test totals after this audit

Server 129 (Domain 49, Application 17, Architecture 4, Integration incl. SignalR and localization 59), client 26, E2E 1.
