using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Users;

namespace Messenger.Application.Auth;

/// <summary>Tokens issued to the client. The raw refresh token goes to an HttpOnly cookie, never to the body.
/// <see cref="RefreshToken"/> is null when a parallel tab refreshed within the grace window (cookie already rotated).</summary>
public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt, MeDto User);

public sealed record VerificationEmailRequested(Guid UserId, string Email, string DisplayName, string RawToken) : IDomainEvent;

public sealed record PasswordResetRequested(Guid UserId, string Email, string DisplayName, string RawToken) : IDomainEvent;

public static class AuthErrors
{
    public static readonly Error InvalidCredentials = new(ErrorType.Unauthorized, "invalid_credentials", "Invalid email or password.");
    public static readonly Error InvalidRefresh = new(ErrorType.Unauthorized, "invalid_refresh_token", "Your session has expired. Please sign in again.");
    public static readonly Error EmailNotVerified = new(ErrorType.Forbidden, "email_not_verified", "Please confirm your email address before signing in.");
    public static readonly Error Suspended = new(ErrorType.Forbidden, "account_suspended", "This account has been suspended.");
    public static readonly Error LockedOut = new(ErrorType.Locked, "account_locked", "Too many failed sign-in attempts. Try again later.");
    public static readonly Error InvalidToken = Error.BadRequest("invalid_token", "This link is invalid.");
    public static readonly Error UsernameTaken = Error.Conflict("username_taken", "This username is already taken.");
}

/// <summary>Creates refresh-token families (sessions) and access tokens.</summary>
public sealed class SessionIssuer(ITokenService tokens, IAuthTokenRepository repository, IClock clock, ICurrentUser currentUser, AuthOptions options)
{
    public AuthTokens StartSession(User user) => Issue(user, Guid.CreateVersion7(clock.UtcNow), previous: null);

    public AuthTokens Rotate(User user, RefreshToken previous) => Issue(user, previous.FamilyId, previous);

    public AuthTokens AccessOnly(User user, Guid familyId)
    {
        var (access, expires) = tokens.CreateAccessToken(user.Id, familyId, user.Role);
        return new AuthTokens(access, expires, null, null, ToMe(user));
    }

    private AuthTokens Issue(User user, Guid familyId, RefreshToken? previous)
    {
        var now = clock.UtcNow;
        var raw = tokens.GenerateOpaqueToken();
        var refresh = RefreshToken.Create(user.Id, familyId, tokens.Hash(raw), now, TimeSpan.FromDays(options.RefreshTtlDays),
            currentUser.UserAgent, currentUser.IpAddress);
        repository.Add(refresh);
        previous?.ReplaceWith(refresh, now);
        var (access, expires) = tokens.CreateAccessToken(user.Id, familyId, user.Role);
        return new AuthTokens(access, expires, raw, refresh.ExpiresAt, ToMe(user));
    }

    public static MeDto ToMe(User user) => new(user.Id, user.Email, user.Username, user.DisplayName, user.Role.ToString(),
        user.EmailConfirmed, user.NeedsProfile, user.PasswordHash is not null, user.CreatedAt);
}

/// <summary>Issues single-use email tokens; the email itself is sent after commit by an event handler.</summary>
public sealed class UserTokenIssuer(ITokenService tokens, IAuthTokenRepository repository, IDomainEventCollector events, IClock clock, AuthOptions options)
{
    public async Task IssueVerificationAsync(User user, CancellationToken ct)
    {
        var raw = await IssueAsync(user, UserTokenType.EmailVerification, TimeSpan.FromHours(options.EmailVerificationTtlHours), ct);
        events.Add(new VerificationEmailRequested(user.Id, user.Email, user.DisplayName, raw));
    }

    public async Task IssuePasswordResetAsync(User user, CancellationToken ct)
    {
        var raw = await IssueAsync(user, UserTokenType.PasswordReset, TimeSpan.FromMinutes(options.PasswordResetTtlMinutes), ct);
        events.Add(new PasswordResetRequested(user.Id, user.Email, user.DisplayName, raw));
    }

    private async Task<string> IssueAsync(User user, UserTokenType type, TimeSpan ttl, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await repository.InvalidateUserTokensAsync(user.Id, type, now, ct);
        var raw = tokens.GenerateOpaqueToken();
        repository.Add(UserToken.Create(user.Id, type, tokens.Hash(raw), now, ttl));
        return raw;
    }
}
