# 2. Domain (`server/src/Messenger.Domain`)

Pure C#: no EF Core, ASP.NET, MediatR or FluentValidation. Entities have private setters and expose behaviour through methods that enforce invariants and return `Result` for expected failures.

## Folder layout

| Path | Contents |
|---|---|
| `Common/Entity.cs` | `Entity` base class collecting domain events; `IDomainEvent` marker |
| `Common/Result.cs` | `Result`, `Result<T>`, `Error`, `ErrorType` |
| `Common/InputRules.cs` | Text normalisation and validation limits |
| `Users/User.cs` | `User`, `UserStatus`, `SystemRole`, events `UserRegistered`, `UserDeleted` |
| `Users/Tokens.cs` | `UserToken`, `RefreshToken`, `ExternalLogin`, `PushSubscription`, `AuditLog` |
| `Conversations/Conversation.cs` | `Conversation` aggregate, `ConversationType`, `ParticipantRole`, `ConversationChanged` |
| `Conversations/ConversationParticipant.cs` | Membership with sequence watermarks |
| `Conversations/GroupPermissions.cs` | Group role matrix |
| `Messages/Message.cs` | `Message`, `MessageEditHistory`, `MessageReceipt`, `MessageAttachment`, `MessageStatusRules`, message events |

## Common

### `Result` and `Error`
Expected failures are values, not exceptions. `Error(Type, Code, Message, Fields?)` with `ErrorType` ∈ `Validation, Unauthorized, Forbidden, NotFound, Conflict, Gone, BadRequest, Locked`. The API maps types to HTTP: Validation→422, Unauthorized→401, Forbidden→403, NotFound→404, Conflict→409, Gone→410, Locked→429, BadRequest→400. Factory helpers: `Error.Validation(field, message)`, `NotFound(what)`, `Forbidden()`, `Conflict(code, message)`, `Unauthorized()`, `Gone(code, message)`, `BadRequest(code, message)`. `Result` and `Result<T>` convert implicitly from `Error` (and `T`).

Messages are written in **English**; the API layer translates them (see [API](05-api.md#localization)).

### `Entity` and domain events
`Entity.Raise(IDomainEvent)` stores events on the aggregate. Infrastructure moves them to a buffer when `SaveChanges` runs and publishes them after commit.

### `InputRules`
| Rule | Value |
|---|---|
| Normalisation | Unicode NFC + trim (`Normalize`) |
| Message body | non-empty, ≤ 4000 user-perceived characters (grapheme clusters), no control/format characters except `\n`, `\r`, `\t` (and ZWJ for emoji) |
| Display name | 1–64 characters, no control characters |
| Username | `^[a-z0-9_.]{3,32}$` after lower-casing |
| Password | ≥ 10 characters, ≤ 72 UTF-8 bytes (BCrypt limit) |
| Group title | 1–100 characters |
| Email | ≤ 254 characters, simple `a@b.c` shape |

`NormalizeEmail` upper-cases, `NormalizeUsername` lower-cases; both are used for the unique indexes.

## Users

### `User`
State: `Email`, `EmailNormalized`, `EmailConfirmed`, `PasswordHash?`, `Username(+Normalized)`, `DisplayName`, `Role` (`User|Admin`), `Status` (`Active|Suspended|Deleted`), `NeedsProfile` (OAuth onboarding), `FailedLoginCount`, `LockoutUntil`, `CreatedAt`, `DeletedAt`, `LastSeenAt`.

| Method | Behaviour |
|---|---|
| `Register(email, username, displayName, passwordHash, now)` | New active user, id = GUID v7, raises `UserRegistered` |
| `ConfirmEmail`, `MarkNeedsProfile`, `PromoteToAdmin` | State changes |
| `RecordFailedLogin(now, max, lockout)` / `RecordSuccessfulLogin` / `IsLockedOut` | Account lockout (default 5 failures → 15 min) |
| `ChangeUsername`, `ChangeDisplayName`, `SetPasswordHash` | Profile and credentials |
| `Suspend` / `Unsuspend` | Admin blocking |
| `Delete(now)` | Irreversible: status `Deleted`, email → `deleted-<id>@deleted.invalid`, username → `deleted-<id>`, display name → "Deleted account", password and confirmation cleared. Frees the username. Raises `UserDeleted` |

### Token entities (`Users/Tokens.cs`)
- **`UserToken`** — single-use email verification / password reset token; only the SHA-256 hash is stored. `Use(now)` returns `Gone` (`token_used` / `token_expired`).
- **`RefreshToken`** — one row per rotation; sessions are token **families** (`FamilyId`). `ReplaceWith` marks rotation; `IsWithinRotationGrace(now, grace)` supports parallel tabs; `Revoke`.
- **`ExternalLogin`** — `(Provider, ProviderUserId)` link for OAuth.
- **`PushSubscription`** — Web Push endpoint and keys; `MarkSuccess`.
- **`AuditLog`** — append-only record of sensitive actions.

## Conversations

### `Conversation` (aggregate root)
- `Type` = `Direct` or `Group`; `DirectKey` = `"{minId}:{maxId}"` for direct chats (`DirectKeyFor(a, b)`, order-independent).
- `LastSequence`, `LastMessageAt` are maintained by SQL in the repository (not by the entity).
- `Participants` is a read-only collection.

| Method | Rule |
|---|---|
| `CreateGroup(creatorId, title, memberIds, now)` | ≥ 3 members including the creator, ≤ 200; creator becomes `Owner` |
| `Rename(actor, title)` | Owner/Admin |
| `AddMembers(actor, userIds, now)` | Owner/Admin; new members see only later messages (`VisibleFromSequence = LastSequence + 1`); re-adding a former member resets watermarks |
| `RemoveMember(actor, target, now)` | Owner removes anyone but owners; Admin removes plain members; removing yourself = `Leave` |
| `ChangeRole(actor, target, role)` | Owner only; ownership cannot be assigned directly; owner's role cannot change |
| `Leave(userId, now)` | Groups only; ownership moves to the oldest admin, else the oldest member; an empty group is soft-deleted |
| `Delete(actor, now)` | Owner only; soft delete (`DeletedAt`), history stays readable |
| `Clear(userId)` | Direct chats only; moves the caller's `ClearedUpToSequence` to `LastSequence` |

All mutating group methods reject deleted groups with `conversation_deleted` (409). Each change raises `ConversationChanged` (with ids of removed users so they are still notified).

### `ConversationParticipant`
`Role`, `JoinedAt`, `VisibleFromSequence`, `ClearedUpToSequence`, `LastReadSequence`, `LeftAt`. `VisibilityFloor = max(VisibleFromSequence − 1, ClearedUpToSequence)`; `CanSee(sequence)` ⇔ `sequence > floor`. `ClearUpTo` and `MarkReadUpTo` never move backwards.

### `GroupPermissions`
`Has(role, permission)` for `Rename`, `ManageMembers`, `ChangeRoles`, `DeleteGroup`, and `CanRemove(actor, target)`. Group admins can **not** edit or delete other people's messages, and system admins cannot read private chats.

## Messages

### `Message`
`Sequence`, `UpdatedSequence`, sender snapshot (`SenderUsernameSnapshot`, `SenderDisplayNameSnapshot`), `Body?`, `ReplyToMessageId?`, `ClientMessageId`, `CreatedAt`, `EditedAt?`, `DeletedAt?`, `EditHistory`, `Attachments`.

| Method | Rule |
|---|---|
| `Create(...)` | Takes the already allocated sequence; `UpdatedSequence = Sequence`; raises `MessageSent` |
| `CanEdit(user, now)` | Author, not deleted, within `EditWindow` = 48 h |
| `Edit(editor, newBody, newSequence, now)` | Author only (403), not deleted (409 `message_deleted`), within 48 h (409 `edit_window_expired`); stores the previous body in `MessageEditHistory`; no-op when unchanged; raises `MessageEdited` |
| `Delete(actor, newSequence, now)` | Author only; tombstone: body = null, history and attachments cleared, `DeletedAt` set; idempotent; raises `MessageDeleted` |

### Other types
- **`MessageEditHistory`** — previous body + timestamp; visible to the author only.
- **`MessageReceipt`** — `(MessageId, UserId, DeliveredAt, ReadAt?)`; rows are created lazily on acknowledgement, so "not delivered" = no row.
- **`MessageAttachment`** — metadata only (name, size ≤ 100 MB, content type); at most 10 per message.
- **`MessageStatusRules.Aggregate(recipients, delivered, read)`** — author-facing status: `Read` only if every recipient read it, `Delivered` only if every recipient received it, else `Sent`.

### Events
`MessageSent`, `MessageEdited`, `MessageDeleted`, `ConversationChanged`, `UserRegistered`, `UserDeleted`.

## Extending the domain
1. Put new rules in the entity as a method returning `Result`; keep setters private.
2. Raise a domain event if other modules must react (realtime, push, email).
3. Add unit tests in `server/tests/Messenger.Domain.Tests`.
4. The architecture test will fail if you add a framework reference.
