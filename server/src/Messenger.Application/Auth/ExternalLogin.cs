using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Messenger.Domain.Users;

namespace Messenger.Application.Auth;

/// <summary>Identity asserted by an OAuth/OIDC provider after the authorization-code exchange.</summary>
public sealed record ExternalIdentity(string Provider, string ProviderUserId, string? Email, bool EmailVerified, string? DisplayName);

/// <summary>AUTH-09 / P-18: link by (provider, sub); auto-merge by email only when both sides verified it.</summary>
public sealed record ExternalLoginCommand(ExternalIdentity Identity) : ICommand<Result<AuthTokens>>, IAllowAnonymous, IRetryOnUniqueViolation;

public sealed class ExternalLoginHandler(IUserRepository users, SessionIssuer sessions, IAuditLogger audit, IClock clock)
    : IRequestHandler<ExternalLoginCommand, Result<AuthTokens>>
{
    public async Task<Result<AuthTokens>> Handle(ExternalLoginCommand request, CancellationToken ct)
    {
        var identity = request.Identity;
        var now = clock.UtcNow;
        var link = await users.FindExternalLoginAsync(identity.Provider, identity.ProviderUserId, ct);
        if (link is not null)
        {
            var linked = await users.GetAsync(link.UserId, ct);
            if (linked is null || linked.Status == UserStatus.Deleted)
            {
                return AuthErrors.InvalidCredentials;
            }

            return linked.Status == UserStatus.Suspended ? AuthErrors.Suspended : Login(linked);
        }

        var email = InputRules.Normalize(identity.Email);
        if (!InputRules.IsValidEmail(email))
        {
            return Error.BadRequest("email_required", "The provider did not share a usable email address.");
        }

        var existing = await users.GetByEmailAsync(InputRules.NormalizeEmail(email), ct);
        if (existing is not null)
        {
            if (!identity.EmailVerified || !existing.EmailConfirmed || !existing.IsActive)
            {
                return Error.Conflict("account_exists",
                    "An account with this email already exists. Sign in with your password and link the provider in settings.");
            }

            users.AddExternalLogin(ExternalLogin.Create(existing.Id, identity.Provider, identity.ProviderUserId, now));
            audit.Record("auth.external_linked", "user", existing.Id.ToString(), new { identity.Provider }, existing.Id);
            return Login(existing);
        }

        var displayName = InputRules.Normalize(identity.DisplayName);
        if (InputRules.ValidateDisplayName(displayName) is not null)
        {
            displayName = email.Split('@')[0];
            displayName = displayName.Length > InputRules.DisplayNameMaxLength ? displayName[..InputRules.DisplayNameMaxLength] : displayName;
        }

        var user = User.Register(email, $"user_{Guid.NewGuid():N}"[..13], displayName, null, now);
        user.MarkNeedsProfile();
        if (identity.EmailVerified)
        {
            user.ConfirmEmail();
        }

        users.Add(user);
        users.AddExternalLogin(ExternalLogin.Create(user.Id, identity.Provider, identity.ProviderUserId, now));
        audit.Record("user.registered", "user", user.Id.ToString(), new { identity.Provider }, user.Id);
        return Login(user);
    }

    private AuthTokens Login(User user)
    {
        user.RecordSuccessfulLogin();
        audit.Record("auth.login", "user", user.Id.ToString(), new { method = "external" }, user.Id);
        return sessions.StartSession(user);
    }
}

/// <summary>Links a provider to an already signed-in account (settings flow).</summary>
public sealed record LinkExternalLoginCommand(Guid UserId, ExternalIdentity Identity) : ICommand<Result>, IAllowAnonymous;

public sealed class LinkExternalLoginHandler(IUserRepository users, IAuditLogger audit, IClock clock) : IRequestHandler<LinkExternalLoginCommand, Result>
{
    public async Task<Result> Handle(LinkExternalLoginCommand request, CancellationToken ct)
    {
        var existing = await users.FindExternalLoginAsync(request.Identity.Provider, request.Identity.ProviderUserId, ct);
        if (existing is not null)
        {
            return existing.UserId == request.UserId
                ? Result.Success()
                : Error.Conflict("external_login_taken", "This provider account is linked to another user.");
        }

        users.AddExternalLogin(ExternalLogin.Create(request.UserId, request.Identity.Provider, request.Identity.ProviderUserId, clock.UtcNow));
        audit.Record("auth.external_linked", "user", request.UserId.ToString(), new { request.Identity.Provider }, request.UserId);
        return Result.Success();
    }
}

/// <summary>Onboarding for OAuth users: choose a username (and optionally a display name).</summary>
public sealed record CompleteProfileCommand(string Username, string? DisplayName) : ICommand<Result<MeDto>>;

public sealed class CompleteProfileValidator : AbstractValidator<CompleteProfileCommand>
{
    public CompleteProfileValidator()
    {
        RuleFor(x => x.Username).ValidUsername();
        When(x => x.DisplayName is not null, () => RuleFor(x => x.DisplayName!).ValidDisplayName());
    }
}

public sealed class CompleteProfileHandler(IUserRepository users, ICurrentUser currentUser) : IRequestHandler<CompleteProfileCommand, Result<MeDto>>
{
    public async Task<Result<MeDto>> Handle(CompleteProfileCommand request, CancellationToken ct)
    {
        var user = await users.GetAsync(currentUser.RequiredUserId, ct);
        if (user is null)
        {
            return Error.Unauthorized();
        }

        var username = InputRules.NormalizeUsername(request.Username);
        if (await users.UsernameTakenAsync(username, user.Id, ct))
        {
            return AuthErrors.UsernameTaken;
        }

        user.ChangeUsername(username);
        if (request.DisplayName is not null)
        {
            user.ChangeDisplayName(request.DisplayName);
        }

        return SessionIssuer.ToMe(user);
    }
}
