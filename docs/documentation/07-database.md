# 7. Database

PostgreSQL 16 (Neon in production, Docker locally). Schema is owned by EF Core migrations in `server/src/Messenger.Infrastructure/Persistence/Migrations`. Names are snake_case.

## Tables

| Table | Key columns | Notes |
|---|---|---|
| `users` | `id`, `email`, `email_normalized`, `email_confirmed`, `password_hash`, `username`, `username_normalized`, `display_name`, `role`, `status`, `needs_profile`, `failed_login_count`, `lockout_until`, `created_at`, `deleted_at`, `last_seen_at` | Partial unique indexes on `email_normalized` and `username_normalized` **where `status <> 'Deleted'`** — deleting an account frees both |
| `external_logins` | `id`, `user_id`, `provider`, `provider_user_id` | Unique `(provider, provider_user_id)` |
| `user_tokens` | `id`, `user_id`, `type`, `token_hash`, `expires_at`, `used_at` | Only the SHA-256 hash is stored; unique `token_hash` |
| `refresh_tokens` | `id`, `user_id`, `family_id`, `token_hash`, `created_at`, `expires_at`, `revoked_at`, `replaced_by_token_id`, `user_agent`, `ip` | A session = `family_id`; indexes on `token_hash` (unique), `family_id`, `user_id` |
| `conversations` | `id`, `type`, `direct_key`, `title`, `created_by_id`, `created_at`, `last_sequence`, `last_message_at`, `deleted_at` | Unique `direct_key` **where not null** (one direct chat per pair) |
| `conversation_participants` | PK `(conversation_id, user_id)`, `role`, `joined_at`, `visible_from_sequence`, `cleared_up_to_sequence`, `last_read_sequence`, `left_at` | Index on `user_id` |
| `messages` | `id`, `conversation_id`, `sequence`, `updated_sequence`, `sender_id`, `sender_*_snapshot`, `body`, `reply_to_message_id`, `client_message_id`, `created_at`, `edited_at`, `deleted_at` | Unique `(conversation_id, sequence)` and `(sender_id, client_message_id)`; index `(conversation_id, updated_sequence)`; trigram index (below) |
| `message_edit_history` | `id`, `message_id`, `previous_body`, `edited_at` | Deleted with the tombstone |
| `message_receipts` | PK `(message_id, user_id)`, `delivered_at`, `read_at` | Created lazily on ack |
| `message_attachments` | `id`, `message_id`, `file_name`, `size_bytes`, `content_type`, `storage_key` | Metadata only |
| `push_subscriptions` | `id`, `user_id`, `endpoint`, `p256dh`, `auth`, `last_success_at` | Unique `endpoint` |
| `audit_logs` | `id`, `actor_user_id`, `action`, `entity_type`, `entity_id`, `data_json (jsonb)`, `ip`, `created_at` | Append-only; index on `created_at` |

Foreign keys: participants → users (restrict), messages → users (restrict) so history survives anonymised accounts; tokens, receipts, subscriptions cascade with the user; messages and participants cascade with the conversation.

## Extension and special index
The initial migration enables `pg_trgm` and runs:

```sql
CREATE INDEX ix_messages_body_trgm ON messages USING gin (lower(body) gin_trgm_ops) WHERE deleted_at IS NULL;
```

Search uses `lower(body) LIKE '%term%'` with the term escaped (`\`, `%`, `_`), so the trigram index is used and wildcards typed by users are literal.

## Raw SQL
| Purpose | Statement |
|---|---|
| Sequence allocation | `UPDATE conversations SET last_sequence = last_sequence + 1, last_message_at = … WHERE id = @id AND deleted_at IS NULL RETURNING last_sequence` |
| Direct chat get-or-create | `INSERT INTO conversations … ON CONFLICT (direct_key) WHERE direct_key IS NOT NULL DO NOTHING` + participant inserts with `ON CONFLICT DO NOTHING` |
| Delivered | `INSERT INTO message_receipts … SELECT … ON CONFLICT DO NOTHING RETURNING message_id` (honours visibility watermarks) |
| Read | `INSERT … ON CONFLICT (message_id, user_id) DO UPDATE SET read_at = COALESCE(read_at, EXCLUDED.read_at)` |
| Migration lock | `pg_advisory_lock(7241102026)` around `Migrate()` |

## Migrations workflow
```bash
dotnet ef migrations add <Name> \
  --project server/src/Messenger.Infrastructure \
  --startup-project server/src/Messenger.Infrastructure \
  --output-dir Persistence/Migrations
```
`DesignTimeDbContextFactory` supplies a connection string only for tooling; no database is contacted. Migrations apply automatically at startup (or run `-- migrate`). No `EnsureCreated`.

## Neon notes
Use the **direct** (non-pooler) host (Npgsql has its own pool), `SSL Mode=Require`, `Maximum Pool Size=10`. `/health` never touches the database so Neon can sleep; `/health/ready` wakes it. The presence tracker writes `last_seen_at` once per disconnect; no background job polls the database.

## Free-tier budget
Neon free = 1 GB per project (checked 2026-11-06; the spec said 0.5 GB), 100 compute-hours a month, compute suspends after 5 minutes idle. The demo seed is a few hundred messages; message bodies are the main growth. Tombstones free the body text.
