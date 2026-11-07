using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Users;

namespace Messenger.Application.Auth;

// AUTH-01: registration answers neutrally when the email is already known (no user enumeration).
public sealed record RegisterCommand(string Email, string Password, string Username, string DisplayName)
    : ICommand<Result>, IAllowAnonymous, IRetryOnUniqueViolation;

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).ValidEmail();
        RuleFor(x => x.Password).ValidPassword();
        RuleFor(x => x.Username).ValidUsername();
        RuleFor(x => x.DisplayName).ValidDisplayName();
    }
}

public sealed class RegisterHandler(IUserRepository users, IPasswordHasher hasher, UserTokenIssuer tokenIssuer,
    IAuditLogger audit, IClock clock, AuthOptions options) : IRequestHandler<RegisterCommand, Result>
{
    public async Task<Result> Handle(RegisterCommand request, CancellationToken ct)
    {
        var existing = await users.GetByEmailAsync(InputRules.NormalizeEmail(request.Email), ct);
        if (existing is not null)
        {
            if (existing is { EmailConfirmed: false, IsActive: true } && options.RequireEmailVerification)
            {
                await tokenIssuer.IssueVerificationAsync(existing, ct);
            }

            return Result.Success();
        }

        if (await users.UsernameTakenAsync(InputRules.NormalizeUsername(request.Username), null, ct))
        {
            return AuthErrors.UsernameTaken;
        }

        var user = User.Register(request.Email, request.Username, request.DisplayName, hasher.Hash(request.Password), clock.UtcNow);
        users.Add(user);
        audit.Record("user.registered", "user", user.Id.ToString(), actorUserId: user.Id);
        if (options.RequireEmailVerification)
        {
            await tokenIssuer.IssueVerificationAsync(user, ct);
        }
        else
        {
            user.ConfirmEmail();
        }

        return Result.Success();
    }
}

// AUTH-03
public sealed record VerifyEmailCommand(string Token) : ICommand<Result>, IAllowAnonymous;

public sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailValidator() => RuleFor(x => x.Token).ValidOpaqueToken();
}

public sealed class VerifyEmailHandler(IAuthTokenRepository tokens, IUserRepository users, ITokenService tokenService, IClock clock)
    : IRequestHandler<VerifyEmailCommand, Result>
{
    public async Task<Result> Handle(VerifyEmailCommand request, CancellationToken ct)
    {
        var token = await tokens.FindUserTokenAsync(tokenService.Hash(request.Token), UserTokenType.EmailVerification, ct);
        if (token is null)
        {
            return AuthErrors.InvalidToken;
        }

        var used = token.Use(clock.UtcNow);
        if (!used.IsSuccess)
        {
            return used;
        }

        var user = await users.GetAsync(token.UserId, ct);
        if (user is null || user.Status == UserStatus.Deleted)
        {
            return AuthErrors.InvalidToken;
        }

        user.ConfirmEmail();
        return Result.Success();
    }
}

public sealed record ResendVerificationCommand(string Email) : ICommand<Result>, IAllowAnonymous;

public sealed class ResendVerificationValidator : AbstractValidator<ResendVerificationCommand>
{
    public ResendVerificationValidator() => RuleFor(x => x.Email).ValidEmail();
}

public sealed class ResendVerificationHandler(IUserRepository users, UserTokenIssuer tokenIssuer)
    : IRequestHandler<ResendVerificationCommand, Result>
{
    public async Task<Result> Handle(ResendVerificationCommand request, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(InputRules.NormalizeEmail(request.Email), ct);
        if (user is { EmailConfirmed: false, IsActive: true })
        {
            await tokenIssuer.IssueVerificationAsync(user, ct);
        }

        return Result.Success();
    }
}

// AUTH-04 / AUTH-10 / AUTH-12
public sealed record LoginCommand(string Email, string Password) : ICommand<Result<AuthTokens>>, IAllowAnonymous, ICommitOnFailure;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(InputRules.EmailMaxLength)
            .Must(e => !InputRules.HasForbiddenControlChars(e, allowLineBreaks: false)).WithMessage("Email is not valid.");
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
    }
}

public sealed class LoginHandler(IUserRepository users, IPasswordHasher hasher, SessionIssuer sessions, IAuditLogger audit,
    IClock clock, AuthOptions options) : IRequestHandler<LoginCommand, Result<AuthTokens>>
{
    // Verifying against a throwaway hash keeps response time similar for unknown emails.
    private static string? _dummyHash;

    public async Task<Result<AuthTokens>> Handle(LoginCommand request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var user = await users.GetByEmailAsync(InputRules.NormalizeEmail(request.Email), ct);
        if (user is null || user.Status == UserStatus.Deleted || user.PasswordHash is null)
        {
            _dummyHash ??= hasher.Hash(Guid.NewGuid().ToString());
            hasher.Verify(request.Password, _dummyHash);
            return AuthErrors.InvalidCredentials;
        }

        if (user.IsLockedOut(now))
        {
            return AuthErrors.LockedOut;
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin(now, options.MaxFailedLogins, TimeSpan.FromMinutes(options.LockoutMinutes));
            audit.Record("auth.login_failed", "user", user.Id.ToString(), actorUserId: user.Id);
            return AuthErrors.InvalidCredentials;
        }

        if (!user.EmailConfirmed && options.RequireEmailVerification)
        {
            return AuthErrors.EmailNotVerified;
        }

        if (user.Status == UserStatus.Suspended)
        {
            return AuthErrors.Suspended;
        }

        user.RecordSuccessfulLogin();
        audit.Record("auth.login", "user", user.Id.ToString(), actorUserId: user.Id);
        return sessions.StartSession(user);
    }
}

// AUTH-05 / P-09: rotation with reuse detection and a grace window for parallel tabs.
public sealed record RefreshCommand(string RefreshToken) : ICommand<Result<AuthTokens>>, IAllowAnonymous, ICommitOnFailure;

public sealed class RefreshHandler(IAuthTokenRepository tokens, IUserRepository users, ITokenService tokenService,
    SessionIssuer sessions, IAuditLogger audit, IClock clock, AuthOptions options) : IRequestHandler<RefreshCommand, Result<AuthTokens>>
{
    public async Task<Result<AuthTokens>> Handle(RefreshCommand request, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (string.IsNullOrEmpty(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return AuthErrors.InvalidRefresh;
        }

        var token = await tokens.FindRefreshTokenAsync(tokenService.Hash(request.RefreshToken), ct);
        if (token is null)
        {
            return AuthErrors.InvalidRefresh;
        }

        var user = await users.GetAsync(token.UserId, ct);
        if (user is null || !user.IsActive)
        {
            await tokens.RevokeFamilyAsync(token.FamilyId, now, ct);
            return AuthErrors.InvalidRefresh;
        }

        if (token.IsActive(now))
        {
            return sessions.Rotate(user, token);
        }

        var grace = TimeSpan.FromSeconds(options.RefreshGraceSeconds);
        if (token.ExpiresAt > now && token.IsWithinRotationGrace(now, grace))
        {
            var replacement = await tokens.GetRefreshTokenAsync(token.ReplacedByTokenId!.Value, ct);
            if (replacement is not null && (replacement.IsActive(now) || replacement.IsWithinRotationGrace(now, grace)))
            {
                return sessions.AccessOnly(user, token.FamilyId);
            }
        }

        if (token.ReplacedByTokenId is not null)
        {
            await tokens.RevokeFamilyAsync(token.FamilyId, now, ct);
            audit.Record("auth.refresh_reuse_detected", "session", token.FamilyId.ToString(), actorUserId: user.Id);
        }

        return AuthErrors.InvalidRefresh;
    }
}

// AUTH-06: logout revokes the current session (token family).
public sealed record LogoutCommand(string? RefreshToken) : ICommand<Result>, IAllowAnonymous;

public sealed class LogoutHandler(IAuthTokenRepository tokens, ITokenService tokenService, IClock clock) : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.RefreshToken) || request.RefreshToken.Length > 200)
        {
            return Result.Success();
        }

        var token = await tokens.FindRefreshTokenAsync(tokenService.Hash(request.RefreshToken), ct);
        if (token is not null)
        {
            await tokens.RevokeFamilyAsync(token.FamilyId, clock.UtcNow, ct);
        }

        return Result.Success();
    }
}
