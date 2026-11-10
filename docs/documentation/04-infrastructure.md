# 4. Infrastructure (`server/src/Messenger.Infrastructure`)

Adapters that implement the ports declared in Application. Everything here may change (database, email provider, hashing library) without touching Domain or Application.

## Folder layout

| Path | Contents |
|---|---|
| `DependencyInjection.cs` | `AddInfrastructure(configuration)` — wires DbContext, repositories, security, email, push |
| `Persistence/MessengerDbContext.cs` | EF model (tables, indexes, conversions) and `IApplicationDbContext` read sets |
| `Persistence/Repositories.cs` | Command-side repositories, `AuditLogger` |
| `Persistence/UnitOfWork.cs` | `UnitOfWork`, `DomainEventBuffer`, `DomainEventInterceptor`, `DomainEventDispatcher` |
| `Persistence/DatabaseMigrator.cs` | Startup migration under an advisory lock |
| `Persistence/DemoSeeder.cs` | Idempotent realistic demo data |
| `Persistence/DesignTimeDbContextFactory.cs` | Used only by `dotnet ef` |
| `Persistence/Migrations/` | EF migrations (`InitialCreate`) |
| `Security/SecurityServices.cs` | `SystemClock`, `BCryptPasswordHasher`, `JwtTokenService`, options |
| `Notifications/EmailSenders.cs` | `SmtpEmailSender`, `BrevoEmailSender`, `NullEmailSender`, `EmailOptions` |
| `Notifications/WebPushSender.cs` | `WebPushSender`, `VapidOptions` |

## Persistence

### `MessengerDbContext`
- PostgreSQL via Npgsql with the snake_case naming convention (`EFCore.NamingConventions`); `EnableRetryOnFailure(3)`.
- Implements `IApplicationDbContext`: every read set is exposed as `AsNoTracking()`.
- Entity ids are GUID v7 produced by the domain, so `ValueGeneratedNever` is applied to every `Id` — otherwise EF would treat new child entities reached through navigations as updates.
- Enums are stored as strings; `audit_logs.data_json` is `jsonb`.
- See [Database](07-database.md) for tables and indexes.

### Repositories
Command side only (queries use the DbContext directly):
- `UserRepository` — lookup by id/normalised email, username availability (ignoring deleted users), OAuth links.
- `AuthTokenRepository` — user tokens; refresh tokens; `RevokeFamilyAsync` / `RevokeAllForUserAsync` use set-based `ExecuteUpdate`.
- `ConversationRepository`
  - `GetOrCreateDirectAsync` — `INSERT … ON CONFLICT (direct_key) DO NOTHING`, then selects the id and inserts both participants with `ON CONFLICT DO NOTHING`. Safe under concurrency.
  - `AllocateSequenceAsync` — `UPDATE conversations SET last_sequence = last_sequence + 1 … RETURNING last_sequence`. Optionally touches `last_message_at`. Returns `null` for missing/deleted conversations.
- `MessageRepository` — `FindByClientIdAsync`; `MarkDeliveredAsync` (`INSERT … ON CONFLICT DO NOTHING RETURNING`, respects watermarks); `MarkReadAsync` (set-based upsert of receipts).
- `PushSubscriptionRepository`, `AuditLogger` (adds `AuditLog` rows in the current transaction, using `ICurrentUser` for actor and IP).

All raw SQL uses interpolated, parameterised APIs (`SqlQuery`, `ExecuteSqlInterpolated`).

### Unit of work and events
- `UnitOfWork.ExecuteInTransactionAsync` runs the action in `BeginTransaction` inside the EF execution strategy. On success (or when the caller says to commit) it saves and commits; otherwise it rolls back and clears the change tracker and event buffer.
- `DomainEventInterceptor` moves events from tracked entities into `DomainEventBuffer` during `SaveChanges`; `IDomainEventCollector` lets handlers add events that no entity raised.
- `DomainEventDispatcher` publishes buffered events **after commit** through MediatR (`DomainEventNotification<T>`), up to 5 rounds (handlers can raise more). Exceptions are logged and swallowed.
- Npgsql unique violations (`23505`) are translated to `UniqueConstraintException`, which `TransactionBehavior` handles for `IRetryOnUniqueViolation`.

### `DatabaseMigrator`
Opens a connection, takes `pg_advisory_lock(724_110_2026)`, applies pending migrations, releases the lock. Runs at API start (`Database:MigrateOnStartup`, default true) and before CLI commands, so parallel instances never migrate simultaneously.

### `DemoSeeder`
`dotnet run --project server/src/Messenger.Api --launch-profile http -- seed` creates 6 users (alice, bohdan, kate, dmytro, iryna and an admin, all `@migly.local`), a group "Room 412 · Study crew" and five personal chats with a few days of natural conversation, an edit, a tombstone, replies, an attachment and mixed read states. Alice has unread messages (5 from Bohdan, 2 from Dmytro, 1 from Iryna) for trying the unread badges and the AI summary; the read state of everyone is the `Unread` table in `DemoSeeder`. It is idempotent (skips if `alice` exists). The password comes from `Seed:Password` (Development sets `Migly-demo-2026`).

## Security

| Class | Behaviour |
|---|---|
| `SystemClock` | `DateTimeOffset.UtcNow` |
| `BCryptPasswordHasher` | `EnhancedHashPassword` (SHA-384 pre-hash), work factor from `PasswordHashing:WorkFactor` (default 11, clamped 4–16); `Verify` returns false on malformed hashes |
| `JwtTokenService` | HS256 access token with only `sub`, `sid`, `role`, `jti`, `exp`; TTL from `Jwt:AccessTtlMinutes` (default 12, clamped 1–60). `GenerateOpaqueToken` = 256-bit CSPRNG, base64url. `Hash` = SHA-256 hex (used for refresh/verification/reset tokens at rest) |
| `JwtOptions` | `Secret` (≥ 32 bytes; the API refuses to start in production without it), `AccessTtlMinutes`, `DevelopmentSecret` fallback only in Development/Testing |

## Notifications

| Class | Selected when | Notes |
|---|---|---|
| `SmtpEmailSender` | `Email:Provider = Mailpit` (default) | Plain SMTP, no auth; `SmtpHost`/`SmtpPort` |
| `BrevoEmailSender` | `Email:Provider = Brevo` | HTTP API, `Email:BrevoApiKey`, sender from `Email:From` |
| `NullEmailSender` | `Email:Provider = None` | Drops emails, logs only the subject |
| `WebPushSender` | always registered | Active only when `Vapid:PublicKey`, `PrivateKey` and `Subject` are all set; maps 404/410 to `PushResult.Gone` |

## Configuration read here

`ConnectionStrings:Default`, `Jwt:*`, `PasswordHashing:WorkFactor`, `Email:*`, `Vapid:*`. See [Operations](10-operations.md).

## Adding an adapter
Implement the port interface, register it in `DependencyInjection.cs`, add configuration to `appsettings*.json` and `.env.example`, and cover it with an integration test (replace via `ApiFactory` if it talks to the outside world).
