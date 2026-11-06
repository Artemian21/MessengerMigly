using System.Text.Json;
using Messenger.Application.Abstractions;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Infrastructure.Persistence;

public sealed class UserRepository(MessengerDbContext db) : IUserRepository
{
    public Task<User?> GetAsync(Guid id, CancellationToken ct) => db.UserSet.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string emailNormalized, CancellationToken ct) =>
        db.UserSet.FirstOrDefaultAsync(u => u.EmailNormalized == emailNormalized && u.Status != UserStatus.Deleted, ct);

    public Task<bool> UsernameTakenAsync(string usernameNormalized, Guid? exceptUserId, CancellationToken ct) =>
        db.UserSet.AnyAsync(u => u.UsernameNormalized == usernameNormalized && u.Status != UserStatus.Deleted && u.Id != exceptUserId, ct);

    public async Task<IReadOnlyList<User>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.UserSet.Where(u => ids.Contains(u.Id)).ToListAsync(ct);

    public void Add(User user) => db.UserSet.Add(user);

    public Task<ExternalLogin?> FindExternalLoginAsync(string provider, string providerUserId, CancellationToken ct) =>
        db.ExternalLoginSet.FirstOrDefaultAsync(l => l.Provider == provider.ToLower() && l.ProviderUserId == providerUserId, ct);

    public void AddExternalLogin(ExternalLogin login) => db.ExternalLoginSet.Add(login);
}

public sealed class AuthTokenRepository(MessengerDbContext db) : IAuthTokenRepository
{
    public void Add(UserToken token) => db.UserTokenSet.Add(token);

    public Task<UserToken?> FindUserTokenAsync(string tokenHash, UserTokenType type, CancellationToken ct) =>
        db.UserTokenSet.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Type == type, ct);

    public Task InvalidateUserTokensAsync(Guid userId, UserTokenType type, DateTimeOffset now, CancellationToken ct) =>
        db.UserTokenSet.Where(t => t.UserId == userId && t.Type == type && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

    public void Add(RefreshToken token) => db.RefreshTokenSet.Add(token);

    public Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokenSet.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public Task<RefreshToken?> GetRefreshTokenAsync(Guid id, CancellationToken ct) =>
        db.RefreshTokenSet.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokenSet.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    public Task RevokeAllForUserAsync(Guid userId, Guid? exceptFamilyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokenSet.Where(t => t.UserId == userId && t.RevokedAt == null && t.FamilyId != exceptFamilyId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
}

public sealed class ConversationRepository(MessengerDbContext db) : IConversationRepository
{
    public Task<Conversation?> GetWithParticipantsAsync(Guid id, CancellationToken ct) =>
        db.ConversationSet.Include(c => c.Participants).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Conversation>> GetGroupsOfUserAsync(Guid userId, CancellationToken ct) =>
        await db.ConversationSet.Include(c => c.Participants)
            .Where(c => c.Type == ConversationType.Group && c.Participants.Any(p => p.UserId == userId && p.LeftAt == null))
            .ToListAsync(ct);

    public void Add(Conversation conversation) => db.ConversationSet.Add(conversation);

    public async Task<Guid> GetOrCreateDirectAsync(Guid userA, Guid userB, DateTimeOffset now, CancellationToken ct)
    {
        var key = Conversation.DirectKeyFor(userA, userB);
        var newId = Guid.CreateVersion7(now);
        var direct = nameof(ConversationType.Direct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO conversations (id, type, direct_key, created_by_id, created_at, last_sequence)
            VALUES ({newId}, {direct}, {key}, {userA}, {now}, 0)
            ON CONFLICT (direct_key) WHERE direct_key IS NOT NULL DO NOTHING
            """, ct);
        var id = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM conversations WHERE direct_key = {key}").SingleAsync(ct);

        var member = nameof(ParticipantRole.Member);
        foreach (var userId in new[] { userA, userB })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO conversation_participants
                    (conversation_id, user_id, role, joined_at, visible_from_sequence, cleared_up_to_sequence, last_read_sequence)
                VALUES ({id}, {userId}, {member}, {now}, 1, 0, 0)
                ON CONFLICT (conversation_id, user_id) DO NOTHING
                """, ct);
        }

        return id;
    }

    public async Task<long?> AllocateSequenceAsync(Guid conversationId, DateTimeOffset now, bool touchLastMessage, CancellationToken ct)
    {
        var values = await db.Database.SqlQuery<long>($"""
            UPDATE conversations
            SET last_sequence = last_sequence + 1,
                last_message_at = CASE WHEN {touchLastMessage} THEN {now} ELSE last_message_at END
            WHERE id = {conversationId} AND deleted_at IS NULL
            RETURNING last_sequence AS "Value"
            """).ToListAsync(ct);
        return values.Count == 0 ? null : values[0];
    }
}

public sealed class MessageRepository(MessengerDbContext db) : IMessageRepository
{
    public Task<Message?> GetAsync(Guid id, CancellationToken ct) =>
        db.MessageSet.Include(m => m.EditHistory).Include(m => m.Attachments).FirstOrDefaultAsync(m => m.Id == id, ct);

    public Task<Message?> FindByClientIdAsync(Guid senderId, Guid clientMessageId, CancellationToken ct) =>
        db.MessageSet.AsNoTracking().FirstOrDefaultAsync(m => m.SenderId == senderId && m.ClientMessageId == clientMessageId, ct);

    public void Add(Message message) => db.MessageSet.Add(message);

    public async Task<IReadOnlyList<Guid>> MarkDeliveredAsync(Guid userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now, CancellationToken ct)
    {
        var ids = messageIds.ToArray();
        return await db.Database.SqlQuery<Guid>($"""
            INSERT INTO message_receipts (message_id, user_id, delivered_at)
            SELECT m.id, {userId}, {now}
            FROM messages m
            JOIN conversation_participants p
              ON p.conversation_id = m.conversation_id AND p.user_id = {userId} AND p.left_at IS NULL
            WHERE m.id = ANY({ids}) AND m.sender_id <> {userId}
              AND m.sequence >= p.visible_from_sequence AND m.sequence > p.cleared_up_to_sequence
            ON CONFLICT (message_id, user_id) DO NOTHING
            RETURNING message_id AS "Value"
            """).ToListAsync(ct);
    }

    public Task<int> MarkReadAsync(Guid userId, Guid conversationId, long floorExclusive, long upToInclusive, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO message_receipts (message_id, user_id, delivered_at, read_at)
            SELECT m.id, {userId}, {now}, {now}
            FROM messages m
            WHERE m.conversation_id = {conversationId} AND m.sequence > {floorExclusive} AND m.sequence <= {upToInclusive}
              AND m.sender_id <> {userId} AND m.deleted_at IS NULL
            ON CONFLICT (message_id, user_id) DO UPDATE SET read_at = COALESCE(message_receipts.read_at, EXCLUDED.read_at)
            """, ct);
}

public sealed class PushSubscriptionRepository(MessengerDbContext db) : IPushSubscriptionRepository
{
    public Task<PushSubscription?> FindByEndpointAsync(string endpoint, CancellationToken ct) =>
        db.PushSubscriptionSet.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);

    public async Task<IReadOnlyList<PushSubscription>> GetForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        await db.PushSubscriptionSet.Where(s => userIds.Contains(s.UserId)).ToListAsync(ct);

    public void Add(PushSubscription subscription) => db.PushSubscriptionSet.Add(subscription);

    public void Remove(PushSubscription subscription) => db.PushSubscriptionSet.Remove(subscription);

    public Task RemoveAllForUserAsync(Guid userId, CancellationToken ct) =>
        db.PushSubscriptionSet.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
}

/// <summary>AUD-01: append-only audit rows saved in the same transaction as the action.</summary>
public sealed class AuditLogger(MessengerDbContext db, ICurrentUser currentUser, IClock clock) : IAuditLogger
{
    public void Record(string action, string entityType, string? entityId, object? data = null, Guid? actorUserId = null) =>
        db.AuditLogSet.Add(AuditLog.Create(actorUserId ?? currentUser.UserId, action, entityType, entityId,
            data is null ? null : JsonSerializer.Serialize(data), currentUser.IpAddress, clock.UtcNow));
}
