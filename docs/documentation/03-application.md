# 3. Application (`server/src/Messenger.Application`)

Use cases as MediatR requests, grouped by feature. Depends only on Domain (plus MediatR, FluentValidation and EF Core abstractions for read-side `IQueryable`). Never references Npgsql, ASP.NET, BCrypt or JWT libraries.

## Folder layout

| Path | Contents |
|---|---|
| `Abstractions/Ports.cs` | Interfaces implemented by Infrastructure / Api |
| `Abstractions/Options.cs` | `AuthOptions`, `AppOptions` |
| `Common/Messaging.cs` | `ICommand<T>`, `IQuery<T>`, marker interfaces, `PagedResult`, `Paging`, `DomainEventNotification<T>` |
| `Common/Behaviors.cs` | Pipeline behaviors |
| `Common/Dtos.cs` | Response DTOs |
| `Common/ValidationRules.cs` | FluentValidation extension rules built on `InputRules` |
| `Auth/` | Registration, login, tokens, password flows, OAuth, email handlers |
| `Users/` | Profile, search, delete account, presence queries |
| `Conversations/` | Groups, members, direct chats, list/get queries |
| `Messages/` | Send, edit, delete, history, receipts, search |
| `Admin/` | User administration and audit log |
| `Push/` | Web Push subscription |
| `Realtime/` | Post-commit event handlers (SignalR fan-out, push) |
| `Resources/EmailTexts*.resx` | Email subjects/bodies (en neutral, uk) |
| `DependencyInjection.cs` | `AddApplication()` |

## Commands, queries and markers

- `ICommand<TResponse>` changes state and runs inside a transaction. `IQuery<TResponse>` never does and uses `AsNoTracking` projections through `IApplicationDbContext`.
- `TResponse` is always a `Result` / `Result<T>`.

| Marker | Effect |
|---|---|
| `IAllowAnonymous` | Skip the authentication check (register, login, refresh, …) |
| `IConversationMemberRequest` (`ConversationId`) | Caller must be an active participant, otherwise 404 |
| `IMessageAccessRequest` (`MessageId`) | Caller must be an active participant who can still see the message (watermarks), otherwise 404 |
| `IAdminRequest` | Caller must have the system `Admin` role, otherwise 403 |
| `IRetryOnUniqueViolation` | A `UniqueConstraintException` re-runs the command once (idempotent send, get-or-create, concurrent register) |
| `ICommitOnFailure` | Commit even when the result is a failure (failed-login counter, refresh reuse detection) |

## Pipeline behaviors (registration order = execution order)

1. **`LoggingBehavior`** — logs request type, outcome and duration. Never logs payloads.
2. **`ValidationBehavior`** — runs all `IValidator<TRequest>`; failures become `Error.Validation` with camelCase field names (→ HTTP 422 with `errors{}`).
3. **`AuthorizationBehavior`** — authentication, `IAdminRequest`, `IConversationMemberRequest`, `IMessageAccessRequest`.
4. **`TransactionBehavior`** — for commands only: `IUnitOfWork.ExecuteInTransactionAsync` (EF execution strategy; rollback on failure unless `ICommitOnFailure`; one retry on unique violation for `IRetryOnUniqueViolation`).

## Ports (`Abstractions/Ports.cs`)

| Port | Purpose | Implemented in |
|---|---|---|
| `IClock` | UTC time | Infrastructure `SystemClock` |
| `ICurrentUser` | Caller id, session id, admin flag, IP, user agent | Api `CurrentUser` |
| `IApplicationDbContext` | Read-side `IQueryable` sets | Infrastructure `MessengerDbContext` |
| `IUnitOfWork` | `SaveChangesAsync`, `ExecuteInTransactionAsync` | Infrastructure `UnitOfWork` |
| `IDomainEventCollector` | Add events not raised by entities (delivered/read) | Infrastructure `DomainEventBuffer` |
| `IUserRepository`, `IAuthTokenRepository`, `IConversationRepository`, `IMessageRepository`, `IPushSubscriptionRepository` | Command-side persistence | Infrastructure repositories |
| `IAuditLogger` | Append audit rows in the current transaction | Infrastructure `AuditLogger` |
| `IPasswordHasher`, `ITokenService` | BCrypt, JWT, opaque tokens, SHA-256 | Infrastructure `Security` |
| `IEmailSender`, `IPushSender` | Outbound email and Web Push | Infrastructure `Notifications` |
| `IPresenceTracker` | `IsOnline(userId)` | Api `PresenceTracker` |
| `IRealtimeNotifier` | Outbound realtime events | Api `SignalRRealtimeNotifier` |

`IConversationRepository` has two special operations: `GetOrCreateDirectAsync` (race-safe `INSERT … ON CONFLICT DO NOTHING` on `direct_key`) and `AllocateSequenceAsync` (atomic counter, row lock, `null` if the conversation is missing or deleted).

## Features

### Auth (`Auth/`)
| Request | Notes |
|---|---|
| `RegisterCommand` | Neutral result for a known email (no enumeration); 409 for a taken username; issues verification token |
| `VerifyEmailCommand`, `ResendVerificationCommand` | Single-use token, 24 h TTL |
| `LoginCommand` | Generic 401 for unknown email or wrong password; lockout; 403 for unverified/suspended; creates a session (refresh family) |
| `RefreshCommand` | Rotation with reuse detection and a grace window (`Auth:RefreshGraceSeconds`, default 20 s) |
| `LogoutCommand` | Revokes the family of the presented cookie |
| `ForgotPasswordCommand`, `ResetPasswordCommand` | Reset revokes **all** sessions and confirms the email |
| `ChangePasswordCommand` | Revokes all sessions except the current one |
| `ListSessionsQuery`, `RevokeSessionCommand` | Active sessions |
| `ExternalLoginCommand`, `LinkExternalLoginCommand`, `CompleteProfileCommand` | OAuth: link by `(provider, sub)`; merge by email only when both sides verified; onboarding username |
| `SessionIssuer`, `UserTokenIssuer` | Helpers creating tokens; `UserTokenIssuer` queues the email event |
| `AuthEmailHandlers` | Sends verification/reset emails after commit, in the request language |

### Users (`Users/`)
`GetMeQuery`, `UpdateProfileCommand`, `ChangeUsernameCommand`, `DeleteAccountCommand` (leaves groups, revokes sessions, removes push subscriptions, anonymises), `SearchUsersQuery` (prefix, min 2 characters, paged), `GetUserQuery`, `GetPresenceQuery` (hub), `PresenceRules` (presence only for users sharing a conversation), `LikePattern` (escapes `%`, `_`, `\`).

### Conversations (`Conversations/`)
Commands: `CreateGroup`, `RenameGroup`, `AddMembers`, `RemoveMember`, `ChangeMemberRole`, `LeaveConversation`, `ClearConversation`, `DeleteGroup`. Queries: `ListConversations`, `GetConversation`, `GetDirectByPeer` (404 = no chat yet), `ListMembers`, `CheckMembership` (used by the hub). `ConversationReader` builds `ConversationDto`s with unread counts, last visible message, peer presence in one grouped query; a cleared direct chat is hidden until a newer message arrives.

### Messages (`Messages/`)
- **`SendMessageCommand` / `SendDirectMessageCommand`** → `MessageSender`: idempotency check → membership and state checks → reply validation → allocate sequence → insert → returns `SendMessageResult(Message, Created)`. The direct variant first calls `GetOrCreateDirectAsync`.
- `EditMessageCommand`, `DeleteMessageCommand` (both take a new sequence from the conversation counter).
- `GetMessagesQuery` — `before` / `after` cursors, limit ≤ 50; `after` uses `UpdatedSequence`.
- `GetMessageHistoryQuery`, `GetMessageInfoQuery` — author only.
- `GetStatusesQuery` — statuses of my messages from a sequence (resync).
- `AckDeliveredCommand` (idempotent), `MarkReadCommand` (set-based up to a sequence) — raise `MessagesDelivered` / `MessagesRead`.
- `SearchMessagesQuery` — case-insensitive substring search over visible, non-deleted messages, paged with snippet.
- `MessageReader` — shared projection: sender (snapshot for deleted accounts), reply preview, attachments, aggregate status for the viewer's own messages.

### AI summary (`Summaries/`)
"What did I miss": a background summary of the caller's unread messages, produced by the Python service in `ai/`.
- **`StartSummaryQuery`** (`IConversationMemberRequest`; a query because nothing is written to the database and a transaction retry must not create a second job). Snapshot at request time: messages after `max(floor, LastReadSequence)` from other people, not deleted, latest `Ai:MaxMessages` (300) read through `MessageReader`, so every watermark applies. Then `ISummaryJobStore.GetOrCreateActive` (one pending job per user and conversation) and `ISummaryJobQueue.TryEnqueue`; a full queue gives 409 `ai_busy`. Nothing unread → status `empty`, no job.
- **`GetSummaryQuery`** — state of a job; only its owner and only through its own conversation, otherwise 404.
- **`SummaryJobProcessor`** — runs one job: `IConversationSummarizer` → `store.Complete/Fail` → `IRealtimeNotifier.SummaryCompletedAsync`. A lost notification is logged, never fatal (the client can `GET`). Logs ids, statuses and counts only.
- Ports: `IConversationSummarizer` (HTTP to Python, in Infrastructure), `ISummaryJobStore` (in memory, TTL `Ai:JobTtlMinutes`, texts dropped when a job finishes), `ISummaryJobQueue` (bounded channel). `SummaryWorker` (a `BackgroundService` in Infrastructure) reads the queue with `Ai:MaxConcurrency` loops. The summary language is taken from the request culture (`Accept-Language`): `uk` or `en`.
- Job failures reach the client as `errorCode` (`provider_unavailable`, `timeout`, `internal`), not as text; the client owns the wording.

### Admin, Push
`AdminListUsersQuery`, `SuspendUserCommand`, `UnsuspendUserCommand`, `ListAuditLogQuery` (all `IAdminRequest`; admins cannot read message content). `GetPushConfigQuery`, `SubscribePushCommand`, `UnsubscribePushCommand`.

### Realtime handlers (`Realtime/RealtimeEventHandlers.cs`)
Run after commit for `MessageSent`, `MessageEdited`, `MessageDeleted`, `MessagesDelivered`, `MessagesRead`, `ConversationChanged`. Each viewer receives their own projection (status only for the author, reply preview per visibility floor). `PushOnMessageSent` sends Web Push only to recipients without an active connection; payload never contains the message body; dead subscriptions (404/410) are removed.

## Pagination helpers
`Paging.Limit(limit)` clamps to 1–50 (default 20); `PagedResult<T>(Items, Total, Next)` returns `Next = offset + limit` while more items exist.

## Adding a use case
1. Create `record MyCommand(...) : ICommand<Result<MyDto>>` (add markers as needed) in the feature folder.
2. Add an `AbstractValidator<MyCommand>` using `ValidationRules` extensions.
3. Implement `IRequestHandler`; return `Error.*` instead of throwing.
4. Add a controller action in Api and an entry in `ApiMessages.uk.resx` for every new user-visible message.
5. Add unit tests (fake ports with NSubstitute) and an integration test.
