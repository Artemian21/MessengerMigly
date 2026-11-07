using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Application.Auth;

// AUTH-07
public sealed record ForgotPasswordCommand(string Email) : ICommand<Result>, IAllowAnonymous;

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() => RuleFor(x => x.Email).ValidEmail();
}

public sealed class ForgotPasswordHandler(IUserRepository users, UserTokenIssuer tokenIssuer) : IRequestHandler<ForgotPasswordCommand, Result>
{
    public async Task<Result> Handle(ForgotPasswordCommand request, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(InputRules.NormalizeEmail(request.Email), ct);
        if (user is { IsActive: true })
        {
            await tokenIssuer.IssuePasswordResetAsync(user, ct);
        }

        return Result.Success();
    }
}

/// <summary>Reset proves email ownership, so it also confirms the email; every session is revoked.</summary>
public sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand<Result>, IAllowAnonymous;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).ValidOpaqueToken();
        RuleFor(x => x.NewPassword).ValidPassword();
    }
}

public sealed class ResetPasswordHandler(IAuthTokenRepository tokens, IUserRepository users, ITokenService tokenService,
    IPasswordHasher hasher, IAuditLogger audit, IClock clock) : IRequestHandler<ResetPasswordCommand, Result>
{
    public async Task<Result> Handle(ResetPasswordCommand request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var token = await tokens.FindUserTokenAsync(tokenService.Hash(request.Token), UserTokenType.PasswordReset, ct);
        if (token is null)
        {
            return AuthErrors.InvalidToken;
        }

        var used = token.Use(now);
        if (!used.IsSuccess)
        {
            return used;
        }

        var user = await users.GetAsync(token.UserId, ct);
        if (user is null || !user.IsActive)
        {
            return AuthErrors.InvalidToken;
        }

        user.SetPasswordHash(hasher.Hash(request.NewPassword));
        user.ConfirmEmail();
        user.RecordSuccessfulLogin();
        await tokens.RevokeAllForUserAsync(user.Id, null, now, ct);
        audit.Record("auth.password_reset", "user", user.Id.ToString(), actorUserId: user.Id);
        return Result.Success();
    }
}

// AUTH-08: the current session survives, all other sessions are revoked.
public sealed record ChangePasswordCommand(string? CurrentPassword, string NewPassword) : ICommand<Result>;

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).MaximumLength(256);
        RuleFor(x => x.NewPassword).ValidPassword();
    }
}

public sealed class ChangePasswordHandler(IUserRepository users, IAuthTokenRepository tokens, IPasswordHasher hasher,
    ICurrentUser currentUser, IAuditLogger audit, IClock clock) : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        var user = await users.GetAsync(currentUser.RequiredUserId, ct);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        if (user.PasswordHash is not null &&
            (request.CurrentPassword is null || !hasher.Verify(request.CurrentPassword, user.PasswordHash)))
        {
            return Error.Validation("currentPassword", "Current password is incorrect.");
        }

        user.SetPasswordHash(hasher.Hash(request.NewPassword));
        await tokens.RevokeAllForUserAsync(user.Id, currentUser.SessionId, clock.UtcNow, ct);
        audit.Record("auth.password_changed", "user", user.Id.ToString());
        return Result.Success();
    }
}

// AUTH-11
public sealed record ListSessionsQuery : IQuery<Result<IReadOnlyList<SessionDto>>>;

public sealed class ListSessionsHandler(IApplicationDbContext db, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<ListSessionsQuery, Result<IReadOnlyList<SessionDto>>>
{
    public async Task<Result<IReadOnlyList<SessionDto>>> Handle(ListSessionsQuery request, CancellationToken ct)
    {
        var userId = currentUser.RequiredUserId;
        var now = clock.UtcNow;
        var active = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .ToListAsync(ct);
        var familyIds = active.Select(t => t.FamilyId).Distinct().ToList();
        var started = await db.RefreshTokens.AsNoTracking()
            .Where(t => familyIds.Contains(t.FamilyId))
            .GroupBy(t => t.FamilyId)
            .Select(g => new { FamilyId = g.Key, Started = g.Min(t => t.CreatedAt) })
            .ToDictionaryAsync(x => x.FamilyId, x => x.Started, ct);

        IReadOnlyList<SessionDto> sessions = active
            .GroupBy(t => t.FamilyId)
            .Select(g => g.OrderByDescending(t => t.CreatedAt).First())
            .Select(t => new SessionDto(t.FamilyId, started.GetValueOrDefault(t.FamilyId, t.CreatedAt), t.CreatedAt, t.ExpiresAt,
                t.UserAgent, t.Ip, t.FamilyId == currentUser.SessionId))
            .OrderByDescending(s => s.LastUsedAt)
            .ToList();
        return Result.Success(sessions);
    }
}

public sealed record RevokeSessionCommand(Guid SessionId) : ICommand<Result>;

public sealed class RevokeSessionHandler(IApplicationDbContext db, IAuthTokenRepository tokens, ICurrentUser currentUser, IClock clock)
    : IRequestHandler<RevokeSessionCommand, Result>
{
    public async Task<Result> Handle(RevokeSessionCommand request, CancellationToken ct)
    {
        var owns = await db.RefreshTokens.AnyAsync(t => t.FamilyId == request.SessionId && t.UserId == currentUser.RequiredUserId, ct);
        if (!owns)
        {
            return Error.NotFound("Session");
        }

        await tokens.RevokeFamilyAsync(request.SessionId, clock.UtcNow, ct);
        return Result.Success();
    }
}
