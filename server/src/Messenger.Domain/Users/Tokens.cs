using Messenger.Domain.Common;

namespace Messenger.Domain.Users;

public enum UserTokenType { EmailVerification, PasswordReset }

/// <summary>Single-use token for email verification / password reset. Only a hash is stored.</summary>
public sealed class UserToken
{
    private UserToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public UserTokenType Type { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    public static UserToken Create(Guid userId, UserTokenType type, string tokenHash, DateTimeOffset now, TimeSpan ttl) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        Type = type,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now.Add(ttl),
    };

    public Result Use(DateTimeOffset now)
    {
        if (UsedAt is not null)
        {
            return Error.Gone("token_used", "This link has already been used.");
        }

        if (ExpiresAt <= now)
        {
            return Error.Gone("token_expired", "This link has expired.");
        }

        UsedAt = now;
        return Result.Success();
    }

    public void Invalidate(DateTimeOffset now) => UsedAt ??= now;
}

/// <summary>Rotating refresh token. A session is a token family (AUTH-05, P-09).</summary>
public sealed class RefreshToken
{
    private RefreshToken() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }
    public string? UserAgent { get; private set; }
    public string? Ip { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public static RefreshToken Create(Guid userId, Guid familyId, string tokenHash, DateTimeOffset now, TimeSpan ttl,
        string? userAgent, string? ip) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        FamilyId = familyId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now.Add(ttl),
        UserAgent = userAgent is { Length: > 256 } ? userAgent[..256] : userAgent,
        Ip = ip,
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void ReplaceWith(RefreshToken next, DateTimeOffset now)
    {
        RevokedAt = now;
        ReplacedByTokenId = next.Id;
    }

    /// <summary>A rotated token presented again within the grace window is a parallel tab, not theft.</summary>
    public bool IsWithinRotationGrace(DateTimeOffset now, TimeSpan grace) =>
        ReplacedByTokenId is not null && RevokedAt is { } revoked && now - revoked <= grace;
}

public sealed class ExternalLogin
{
    private ExternalLogin() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = null!;
    public string ProviderUserId { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static ExternalLogin Create(Guid userId, string provider, string providerUserId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        Provider = provider.ToLowerInvariant(),
        ProviderUserId = providerUserId,
        CreatedAt = now,
    };
}

public sealed class PushSubscription
{
    private PushSubscription() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string P256dh { get; private set; } = null!;
    public string Auth { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }

    public static PushSubscription Create(Guid userId, string endpoint, string p256dh, string auth, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        Endpoint = endpoint,
        P256dh = p256dh,
        Auth = auth,
        CreatedAt = now,
    };

    public void Update(Guid userId, string p256dh, string auth)
    {
        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
    }

    public void MarkSuccess(DateTimeOffset now) => LastSuccessAt = now;
}

public sealed class AuditLog
{
    private AuditLog() { }

    public Guid Id { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string EntityType { get; private set; } = null!;
    public string? EntityId { get; private set; }
    public string? DataJson { get; private set; }
    public string? Ip { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditLog Create(Guid? actorUserId, string action, string entityType, string? entityId, string? dataJson,
        string? ip, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ActorUserId = actorUserId,
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        DataJson = dataJson,
        Ip = ip,
        CreatedAt = now,
    };
}
