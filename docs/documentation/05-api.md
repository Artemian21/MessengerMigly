# 5. API (`server/src/Messenger.Api`)

ASP.NET Core host and composition root. Contains no business rules: controllers translate HTTP to MediatR requests and `Result` to HTTP.

## Folder layout

| Path | Contents |
|---|---|
| `Program.cs` | Entry point: bootstrap logger, build, CLI commands, migrate, run |
| `ApiSetup.cs` | DI registration (`AddMessengerServices`), authentication, middleware order (`UseMessengerPipeline`), `Cli`, `LoggingSetup` |
| `Controllers/` | `AuthController`, `ExternalAuthController`, `UsersController`, `ConversationsController`, `MessagesController` (+ `SearchController`, `PushController`, `AdminController`), `HealthController`, `ApiControllerBase` |
| `Contracts/Requests.cs` | Request/response records for the HTTP surface |
| `Infrastructure/` | `CurrentUser`, `Problems` (+ `GlobalExceptionHandler`), `HttpSecurity` (rate limiting, security headers, CSRF filter, refresh cookie), `OpenApiSetup`, `ApiLocalizer` |
| `Realtime/` | `ChatHub`, `PresenceTracker`, `SignalRRealtimeNotifier` — see [Realtime](06-realtime.md) |
| `Resources/ApiMessages*.resx` | Translations of API messages (en neutral, uk) |
| `appsettings.json`, `appsettings.Development.json` | Defaults and development overrides |

## Startup

1. Bootstrap Serilog (console, compact JSON).
2. `Cli.IsCommand` strips `migrate | seed | promote <email>` from the arguments; those commands run against the built host and exit (`Cli.RunAsync`).
3. Otherwise the database is migrated (unless `Database:MigrateOnStartup=false`) and the app runs.
4. In production the app **refuses to start** without a `Jwt:Secret` of at least 32 bytes.

## Middleware order (`UseMessengerPipeline`)

`RequestLocalization` → `ForwardedHeaders` → `ExceptionHandler` → `StatusCodePages` → `Hsts` (non-dev) → `SecurityHeaders` → `SerilogRequestLogging` → `DefaultFiles/StaticFiles` → `Routing` → `Cors` → `Authentication` → `Authorization` → `RateLimiter` → OpenAPI (`/openapi/v1.json`) and Scalar (`/docs`) → controllers → hub (`/hubs/chat`) → `/api/**` JSON 404 → SPA fallback to `index.html`.

## Authentication

- **JWT bearer** (HS256, no inbound claim mapping, 30 s clock skew). SignalR passes the token as `?access_token=` for `/hubs/chat` only.
- **Refresh token**: HttpOnly cookie `rt`, `Secure`, `SameSite=Strict`, path `/api/v1/auth`. `/auth/refresh` and `/auth/logout` carry `[RequireSameOrigin]`: the header `X-Requested-With` is mandatory and `Origin` / `Sec-Fetch-Site` must be same-origin.
- **OAuth** (Google, GitHub): registered only when `Authentication:<Provider>:ClientId` is set; a short-lived `External` cookie carries the identity to `/api/v1/auth/external/complete`.
- `CurrentUser` implements `ICurrentUser` from the principal; hub methods set it explicitly (`CurrentUser.Set`).

## Endpoints (`/api/v1`)

Full, always-current reference with request/response examples: **`/docs`** (Scalar) and `/openapi/v1.json`. Summary:

| Area | Routes |
|---|---|
| Auth | `POST auth/register`, `verify-email`, `resend-verification`, `login`, `refresh`, `logout`, `forgot-password`, `reset-password`, `change-password`; `GET auth/sessions`; `DELETE auth/sessions/{id}`; `POST auth/external/complete-profile`; `GET auth/external/providers`, `{provider}/login`, `complete` |
| Users | `GET users/me`; `PATCH users/me`; `PUT users/me/username`; `DELETE users/me`; `GET users?q=&limit=&offset=`; `GET users/{id}` |
| Conversations | `GET conversations`; `GET conversations/{id}`; `GET conversations/direct/{peerUserId}`; `POST conversations/direct/{peerUserId}/messages`; `POST conversations/groups`; `PATCH conversations/{id}`; `POST conversations/{id}/clear`; `POST conversations/{id}/leave`; `DELETE conversations/{id}`; `GET/POST conversations/{id}/members`; `PATCH/DELETE conversations/{id}/members/{userId}` |
| Messages | `GET/POST conversations/{id}/messages`; `POST conversations/{id}/read`; `GET conversations/{id}/statuses`; `PATCH/DELETE messages/{id}`; `GET messages/{id}/history`, `info`; `POST messages/delivered` |
| AI summary | `POST conversations/{id}/summary` (202 + `jobId`; 200 `status: "empty"` when nothing is unread); `GET conversations/{id}/summary/{jobId}` (`pending` / `ready` / `failed` + `errorCode`). 404 when `Ai:Enabled` is false |
| Search | `GET search/messages?q=&conversationId=&limit=&offset=` |
| Push | `GET push/config`; `POST/DELETE push/subscriptions` |
| Admin | `GET admin/users`; `POST admin/users/{id}/suspend`, `unsuspend`; `GET admin/audit` |
| Ops (no `/api/v1`) | `GET /health` (no DB), `GET /health/ready` (`SELECT 1`), `/docs`, `/openapi/v1.json` |

Conventions: JSON camelCase, ISO-8601 UTC timestamps. Lists return `{items,total,next}`; message history uses cursors (`before`, `after`, `limit ≤ 50`). Send endpoints return **201** for a new message and **200** for an idempotent retry.

## Error format (`Infrastructure/Problems.cs`)

All errors are RFC 9457 `application/problem+json` with `type`, `title`, `status`, `detail`, `code` and `traceId`. Validation failures are **422** with an `errors{field:[messages]}` map (this replaces ASP.NET's default 400). Malformed JSON is also 422. `GlobalExceptionHandler` hides internals: database/timeouts → 503 with `Retry-After`, unique-violation races → 409, everything else → 500 with a generic message and a logged exception.

## Rate limiting (`HttpSecurity.cs`)

| Policy | Default | Applies to |
|---|---|---|
| Global | 60 / min / IP (fixed window; Development 300) | everything except `/health` |
| `auth` | 10 / min / IP | register, verify-email, resend-verification, login, forgot/reset-password |
| `send-message` | token bucket, burst 10, 30 / min per user | message-send endpoints |
| `ai-summary` | 5 / min per user (fixed window) | `POST conversations/{id}/summary` (each call costs an LLM request) |

Rejections return 429 with `Retry-After` and a problem document. Settings: `RateLimiting:*`. Behind a proxy set `ForwardedHeaders:TrustProxy=true` so the real client IP is used.

## Security headers
`X-Content-Type-Options`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`, and a strict CSP (`script-src 'self'`; `style-src` allows inline styles for React). `/docs` is exempt from the CSP. HSTS outside Development. CORS allows only `Cors:AllowedOrigins` (empty in production because the app is same-origin).

## OpenAPI
`OpenApiSetup` adds title, description, the Bearer scheme, per-operation security and 401/429 responses, and realistic request examples for every request record. Endpoint summaries come from `[EndpointSummary]` / `[EndpointDescription]` attributes on actions.

## Localization

`ApiLocalizer` translates messages **at the boundary** so Domain/Application stay English-only and framework-free.
- Language from `Accept-Language`: `en` (default) and `uk`; any other value falls back to English.
- `.resx` resource names are the English texts (gettext style). `ApiMessages.resx` is the neutral file, `ApiMessages.uk.resx` the Ukrainian one.
- Parameterised patterns are handled: `"{0} was not found."` (entity name translated too) and ASP.NET's `"The {0} field is required."`.
- Applies to ProblemDetails `title`/`detail`, per-field validation messages and neutral 202 messages.
- Verification and reset emails use `EmailTexts*.resx` from Application with the same culture.

To add a message: write the English text in Domain/Application, then add the same text as `name` and its Ukrainian translation as `value` in `ApiMessages.uk.resx` (and the neutral `ApiMessages.resx`).

## CLI commands
```bash
dotnet run --project server/src/Messenger.Api --launch-profile http -- migrate
dotnet run --project server/src/Messenger.Api --launch-profile http -- seed          # needs Seed:Password
dotnet run --project server/src/Messenger.Api --launch-profile http -- promote you@example.com
```

## Adding an endpoint
1. Request/response record in `Contracts/Requests.cs` (and an example in `OpenApiSetup.Examples`).
2. Action in the right controller: `Send(new MyCommand(...), ct)`; add `[EnableRateLimiting(...)]` if needed; `[EndpointSummary]` and `[ProducesResponseType]` for the docs.
3. Never put authorization checks in the controller — use the Application markers.
