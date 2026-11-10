using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;

namespace Messenger.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid? SessionId { get; }
    bool IsAdmin { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }

    Guid RequiredUserId => UserId ?? throw new InvalidOperationException("No authenticated user.");
}

/// <summary>Read side (CQRS): queries project directly from these sets with AsNoTracking.</summary>
public interface IApplicationDbContext
{
    IQueryable<User> Users { get; }
    IQueryable<Conversation> Conversations { get; }
    IQueryable<ConversationParticipant> Participants { get; }
    IQueryable<Message> Messages { get; }
    IQueryable<MessageEditHistory> MessageEditHistory { get; }
    IQueryable<MessageReceipt> MessageReceipts { get; }
    IQueryable<MessageAttachment> MessageAttachments { get; }
    IQueryable<RefreshToken> RefreshTokens { get; }
    IQueryable<AuditLog> AuditLogs { get; }
    IQueryable<ExternalLogin> ExternalLogins { get; }
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Runs <paramref name="action"/> in a DB transaction inside the retrying execution strategy.
    /// Domain events are dispatched only after a successful commit.</summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, Func<T, bool> shouldCommit, CancellationToken ct);
}

/// <summary>Collects events that are not raised by tracked entities (receipts, read marks).</summary>
public interface IDomainEventCollector
{
    void Add(IDomainEvent domainEvent);
}

/// <summary>Thrown by infrastructure when a unique index rejects a write (concurrent duplicate).</summary>
public sealed class UniqueConstraintException(string? constraint, Exception inner)
    : Exception("A unique constraint was violated.", inner)
{
    public string? Constraint { get; } = constraint;
}

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken ct);
    Task<User?> GetByEmailAsync(string emailNormalized, CancellationToken ct);
    Task<bool> UsernameTakenAsync(string usernameNormalized, Guid? exceptUserId, CancellationToken ct);
    Task<IReadOnlyList<User>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    void Add(User user);
    Task<ExternalLogin?> FindExternalLoginAsync(string provider, string providerUserId, CancellationToken ct);
    void AddExternalLogin(ExternalLogin login);
}

public interface IAuthTokenRepository
{
    void Add(UserToken token);
    Task<UserToken?> FindUserTokenAsync(string tokenHash, UserTokenType type, CancellationToken ct);
    Task InvalidateUserTokensAsync(Guid userId, UserTokenType type, DateTimeOffset now, CancellationToken ct);

    void Add(RefreshToken token);
    Task<RefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken ct);
    Task<RefreshToken?> GetRefreshTokenAsync(Guid id, CancellationToken ct);
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct);
    Task RevokeAllForUserAsync(Guid userId, Guid? exceptFamilyId, DateTimeOffset now, CancellationToken ct);
}

public interface IConversationRepository
{
    Task<Conversation?> GetWithParticipantsAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Conversation>> GetGroupsOfUserAsync(Guid userId, CancellationToken ct);
    void Add(Conversation conversation);

    /// <summary>P-13: race-safe get-or-create (INSERT … ON CONFLICT DO NOTHING on the DirectKey index).</summary>
    Task<Guid> GetOrCreateDirectAsync(Guid userA, Guid userB, DateTimeOffset now, CancellationToken ct);

    /// <summary>P-03: atomic per-conversation counter; the row lock serializes senders until commit.
    /// Returns null when the conversation does not exist or is deleted.</summary>
    Task<long?> AllocateSequenceAsync(Guid conversationId, DateTimeOffset now, bool touchLastMessage, CancellationToken ct);
}

public interface IMessageRepository
{
    Task<Message?> GetAsync(Guid id, CancellationToken ct);
    Task<Message?> FindByClientIdAsync(Guid senderId, Guid clientMessageId, CancellationToken ct);
    void Add(Message message);

    /// <summary>STS-02: idempotent; returns ids of messages that were newly marked delivered.</summary>
    Task<IReadOnlyList<Guid>> MarkDeliveredAsync(Guid userId, IReadOnlyCollection<Guid> messageIds, DateTimeOffset now, CancellationToken ct);

    /// <summary>STS-03: set-based read marking of other people's visible messages in (floor, upTo].</summary>
    Task<int> MarkReadAsync(Guid userId, Guid conversationId, long floorExclusive, long upToInclusive, DateTimeOffset now, CancellationToken ct);
}

public interface IPushSubscriptionRepository
{
    Task<PushSubscription?> FindByEndpointAsync(string endpoint, CancellationToken ct);
    Task<IReadOnlyList<PushSubscription>> GetForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct);
    void Add(PushSubscription subscription);
    void Remove(PushSubscription subscription);
    Task RemoveAllForUserAsync(Guid userId, CancellationToken ct);
}

public interface IAuditLogger
{
    void Record(string action, string entityType, string? entityId, object? data = null, Guid? actorUserId = null);
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public interface ITokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(Guid userId, Guid sessionId, SystemRole role);

    /// <summary>256-bit CSPRNG token, URL-safe.</summary>
    string GenerateOpaqueToken();

    /// <summary>SHA-256 hex; only hashes of opaque tokens are stored.</summary>
    string Hash(string token);
}

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed record PushPayload(string Title, string Body, string Url, string Tag);

public enum PushResult { Sent, Gone, Failed }

public interface IPushSender
{
    bool IsEnabled { get; }
    string? PublicKey { get; }
    Task<PushResult> SendAsync(PushSubscription subscription, PushPayload payload, CancellationToken ct);
}

/// <summary>In-memory presence (PRS-01/02). Implemented next to the SignalR hub.</summary>
public interface IPresenceTracker
{
    bool IsOnline(Guid userId);
}

/// <summary>Outbound realtime events (doc 02 §7). Implemented with SignalR in the API layer.</summary>
public interface IRealtimeNotifier
{
    Task MessageReceivedAsync(IReadOnlyCollection<Guid> userIds, object message, CancellationToken ct);
    Task MessageUpdatedAsync(IReadOnlyCollection<Guid> userIds, object message, CancellationToken ct);
    Task MessageDeletedAsync(IReadOnlyCollection<Guid> userIds, Guid conversationId, Guid messageId, long sequence, long updatedSequence, CancellationToken ct);
    Task MessageStatusChangedAsync(Guid authorId, Guid conversationId, Guid messageId, string status, CancellationToken ct);
    Task ConversationUpdatedAsync(IReadOnlyCollection<Guid> userIds, Guid conversationId, CancellationToken ct);
    Task ConversationReadAsync(Guid userId, Guid conversationId, long lastReadSequence, CancellationToken ct);
}
