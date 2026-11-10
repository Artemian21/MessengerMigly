# 10. Operations

## Configuration

ASP.NET Core maps `Section__Key` environment variables to `Section:Key`. `.env.example` documents every variable; never commit real values (`.env*` is git-ignored, gitleaks runs in CI).

| Variable | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | local compose DB (Development) | PostgreSQL. Neon: direct host, `SSL Mode=Require`, `Maximum Pool Size=10` |
| `Jwt__Secret` | — (required in production, ≥ 32 bytes) | HS256 signing secret: `openssl rand -base64 48` |
| `Jwt__AccessTtlMinutes` | 12 | Access token lifetime |
| `Auth__RefreshTtlDays` / `Auth__RefreshGraceSeconds` | 21 / 20 | Session length; parallel-tab grace |
| `Auth__EmailVerificationTtlHours` / `Auth__PasswordResetTtlMinutes` | 24 / 45 | One-time token lifetimes |
| `Auth__MaxFailedLogins` / `Auth__LockoutMinutes` | 5 / 15 | Brute-force lockout |
| `Auth__RequireEmailVerification` | true | Require confirmed email to sign in |
| `PasswordHashing__WorkFactor` | 11 | BCrypt cost |
| `Email__Provider` | `Mailpit` | `Mailpit` (SMTP), `Brevo` (HTTP API), `None` |
| `Email__From`, `Email__SmtpHost`, `Email__SmtpPort`, `Email__BrevoApiKey` | see `.env.example` | Email settings |
| `Vapid__PublicKey/PrivateKey/Subject` | empty | Web Push (disabled when empty) |
| `Authentication__Google__ClientId/ClientSecret`, `Authentication__GitHub__…` | empty | OAuth (provider hidden when empty) |
| `App__PublicBaseUrl` | `http://localhost:5000` (Dev: `:5173`) | Base URL in email links |
| `ForwardedHeaders__TrustProxy` | false | `true` behind Render's proxy (real client IPs) |
| `RateLimiting__GlobalPerMinute/AuthPerMinute/SendBurst/SendPerMinute` | 60 / 10 / 10 / 30 | Rate limits |
| `Presence__GraceSeconds` | 7 | Delay before "offline" |
| `Database__MigrateOnStartup` | true | Apply migrations at start |
| `Cors__AllowedOrigins` | empty | Only for a cross-origin front end |
| `Seed__Password` | — (Dev: `Migly-demo-2026`) | Password for demo accounts |

## Local environment
`docker-compose.yml` provides `postgres` (16-alpine, db/user/password `messenger`, port 5432, healthcheck) and `mailpit` (SMTP 1025, UI 8025).

```bash
docker compose up -d postgres mailpit
dotnet run --project server/src/Messenger.Api --launch-profile http
cd client && npm install && npm run dev
```
`.claude/launch.json` defines `api` and `client` preview servers for the desktop app.

## Docker image (`Dockerfile`)
Three stages: Node 22 builds the SPA → .NET 9 SDK publishes the API → ASP.NET 9 runtime image with the SPA copied to `wwwroot`. Runs as the non-root `app` user, listens on `$PORT` (default 10000), healthcheck on `/health`. `.dockerignore` keeps `node_modules`, `bin`, `obj`, docs and local files out of the context.

```bash
docker build -t messenger .
docker run --rm -p 10000:10000 --add-host=host.docker.internal:host-gateway \
  -e ConnectionStrings__Default="Host=host.docker.internal;Database=messenger;Username=messenger;Password=messenger" \
  -e Jwt__Secret="$(openssl rand -base64 48)" -e Email__SmtpHost=host.docker.internal messenger
```

## CI (`.github/workflows/ci.yml`)
Runs on every push and pull request:
1. **server** — restore, `dotnet build -c Release`, `dotnet test` (Testcontainers uses Docker on `ubuntu-latest`), uploads results.
2. **client** — `npm ci`, lint, tests, build.
3. **docker** — builds the image (after server and client pass).
4. **secrets** — gitleaks (`.gitleaks.toml` allowlists local development defaults only).

Render auto-deploys after checks pass (`autoDeployTrigger: checksPass` in `render.yaml`).

## Deployment (Render + Neon + Brevo)
Step-by-step runbook with the manual steps marked **HUMAN STEP**: [`docs/DEPLOYMENT.md`](../DEPLOYMENT.md); the ordered to-do list: [`docs/HUMAN_TODO.md`](../HUMAN_TODO.md). `render.yaml` lists the required environment variables; secrets use `sync: false` so they are entered in the Render dashboard on first creation. Optional ones (VAPID, OAuth) are added later in the dashboard. Click-by-click guide: [`docs/DEPLOY_STEP_BY_STEP.md`](../DEPLOY_STEP_BY_STEP.md).

Free-tier behaviour to expect: Render sleeps after 15 min (≈ 50 s cold start; the SPA shows a "server is waking up" banner and SignalR reconnects), Neon pauses after 5 min idle and has 1 GB, Brevo allows 300 emails/day, and the app is single-instance (presence and groups are in memory).

## Scripts
`scripts/smoke.sh <base-url> [mailpit-url]` — checks `/health`, `/health/ready`, `/docs`; registers and verifies a fresh account through Mailpit (or uses `SMOKE_EMAIL`/`SMOKE_PASSWORD`), logs in, calls `/users/me` and expects 401 without a token.

## Logging and diagnostics
- Serilog compact JSON to stdout. Request logs contain method, path (never the query string), status, duration, `UserId` and `TraceId`. Health requests are logged at verbose level.
- Never logged: passwords, tokens, cookies, `Authorization`, message bodies, hub query strings. MediatR logging records only the request type and outcome.
- Every error response carries `traceId` to correlate with logs.
- `/health` = process alive (no DB). `/health/ready` = `SELECT 1`, 503 when the database is unreachable.

## Security checklist
HttpOnly + SameSite=Strict refresh cookie with CSRF header check; JWT secret from environment and mandatory in production; BCrypt hashes; refresh/verification tokens stored as SHA-256; rate limits and lockout; per-resource membership checks; security headers and CSP; plain-text message rendering; parameterised SQL; no secrets in git.

## Maintenance tasks

| Task | Command |
|---|---|
| Apply migrations manually | `dotnet run --project server/src/Messenger.Api -- migrate` |
| Create a system admin | register normally, then `… -- promote you@example.com` |
| Seed demo data | `… -- seed` (needs `Seed__Password`) |
| Reset local database | `docker compose down -v && docker compose up -d postgres mailpit` |
| Rotate the JWT secret | change `Jwt__Secret`; all access tokens expire within minutes; revoke sessions if compromise is suspected |
