using Messenger.Application.Abstractions;
using Messenger.Application.Auth;
using Messenger.Domain.Common;
using Messenger.Domain.Users;
using NSubstitute;
using Shouldly;

namespace Messenger.Application.Tests;

public sealed class AuthHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IAuthTokenRepository _tokens = Substitute.For<IAuthTokenRepository>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IAuditLogger _audit = Substitute.For<IAuditLogger>();
    private readonly AuthOptions _options = new();

    public AuthHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _tokenService.Hash(Arg.Any<string>()).Returns(c => "h:" + c.Arg<string>());
        _tokenService.GenerateOpaqueToken().Returns("new-raw");
        _tokenService.CreateAccessToken(default, default, default).ReturnsForAnyArgs(("jwt", Now.AddMinutes(12)));
    }

    private SessionIssuer Sessions() => new(_tokenService, _tokens, _clock, Substitute.For<ICurrentUser>(), _options);

    private static User ConfirmedUser()
    {
        var user = User.Register("alice@example.com", "alice", "Alice", "hash", Now.AddDays(-1));
        user.ConfirmEmail();
        return user;
    }

    [Fact]
    public async Task Wrong_password_is_generic_401_and_counts_towards_lockout()
    {
        var user = ConfirmedUser();
        _users.GetByEmailAsync(default!, default).ReturnsForAnyArgs(user);
        _hasher.Verify(default!, default!).ReturnsForAnyArgs(false);
        var handler = new LoginHandler(_users, _hasher, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new LoginCommand("alice@example.com", "nope"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
        user.FailedLoginCount.ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_error_as_wrong_password()
    {
        _users.GetByEmailAsync(default!, default).ReturnsForAnyArgs((User?)null);
        _hasher.Hash(default!).ReturnsForAnyArgs("dummy");
        var handler = new LoginHandler(_users, _hasher, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new LoginCommand("ghost@example.com", "whatever-123"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
    }

    [Fact]
    public async Task Unverified_email_cannot_sign_in()
    {
        var user = User.Register("bob@example.com", "bob", "Bob", "hash", Now);
        _users.GetByEmailAsync(default!, default).ReturnsForAnyArgs(user);
        _hasher.Verify(default!, default!).ReturnsForAnyArgs(true);
        var handler = new LoginHandler(_users, _hasher, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new LoginCommand("bob@example.com", "S3cure-pass-123"), CancellationToken.None);

        result.Error!.Code.ShouldBe("email_not_verified");
    }

    [Fact]
    public async Task Reusing_a_rotated_token_outside_grace_revokes_the_family()
    {
        var user = ConfirmedUser();
        var old = RefreshToken.Create(user.Id, Guid.NewGuid(), "h:old", Now.AddMinutes(-10), TimeSpan.FromDays(21), null, null);
        var next = RefreshToken.Create(user.Id, old.FamilyId, "h:next", Now.AddMinutes(-5), TimeSpan.FromDays(21), null, null);
        old.ReplaceWith(next, Now.AddMinutes(-5));
        _tokens.FindRefreshTokenAsync("h:old", default).ReturnsForAnyArgs(old);
        _users.GetAsync(user.Id, default).ReturnsForAnyArgs(user);
        var handler = new RefreshHandler(_tokens, _users, _tokenService, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new RefreshCommand("old"), CancellationToken.None);

        result.Error.ShouldBe(AuthErrors.InvalidRefresh);
        await _tokens.Received(1).RevokeFamilyAsync(old.FamilyId, Now, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Parallel_tab_within_grace_gets_access_token_without_new_cookie()
    {
        var user = ConfirmedUser();
        var old = RefreshToken.Create(user.Id, Guid.NewGuid(), "h:old", Now.AddMinutes(-10), TimeSpan.FromDays(21), null, null);
        var next = RefreshToken.Create(user.Id, old.FamilyId, "h:next", Now.AddSeconds(-5), TimeSpan.FromDays(21), null, null);
        old.ReplaceWith(next, Now.AddSeconds(-5));
        _tokens.FindRefreshTokenAsync(default!, default).ReturnsForAnyArgs(old);
        _tokens.GetRefreshTokenAsync(next.Id, default).ReturnsForAnyArgs(next);
        _users.GetAsync(default, default).ReturnsForAnyArgs(user);
        var handler = new RefreshHandler(_tokens, _users, _tokenService, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new RefreshCommand("old"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.RefreshToken.ShouldBeNull();
        await _tokens.DidNotReceiveWithAnyArgs().RevokeFamilyAsync(default, default, default);
    }

    [Fact]
    public async Task Active_refresh_token_is_rotated()
    {
        var user = ConfirmedUser();
        var current = RefreshToken.Create(user.Id, Guid.NewGuid(), "h:cur", Now.AddMinutes(-1), TimeSpan.FromDays(21), null, null);
        _tokens.FindRefreshTokenAsync(default!, default).ReturnsForAnyArgs(current);
        _users.GetAsync(default, default).ReturnsForAnyArgs(user);
        var handler = new RefreshHandler(_tokens, _users, _tokenService, Sessions(), _audit, _clock, _options);

        var result = await handler.Handle(new RefreshCommand("cur"), CancellationToken.None);

        result.Value.RefreshToken.ShouldBe("new-raw");
        current.ReplacedByTokenId.ShouldNotBeNull();
        current.IsActive(Now).ShouldBeFalse();
    }

    [Fact]
    public async Task Register_with_known_email_is_neutral()
    {
        _users.GetByEmailAsync(default!, default).ReturnsForAnyArgs(ConfirmedUser());
        var issuer = new UserTokenIssuer(_tokenService, _tokens, Substitute.For<IDomainEventCollector>(), _clock, _options);
        var handler = new RegisterHandler(_users, _hasher, issuer, _audit, _clock, _options);

        var result = await handler.Handle(new RegisterCommand("alice@example.com", "S3cure-pass-123", "other", "Other"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _users.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Register_with_taken_username_conflicts()
    {
        _users.GetByEmailAsync(default!, default).ReturnsForAnyArgs((User?)null);
        _users.UsernameTakenAsync(default!, default, default).ReturnsForAnyArgs(true);
        var issuer = new UserTokenIssuer(_tokenService, _tokens, Substitute.For<IDomainEventCollector>(), _clock, _options);
        var handler = new RegisterHandler(_users, _hasher, issuer, _audit, _clock, _options);

        var result = await handler.Handle(new RegisterCommand("new@example.com", "S3cure-pass-123", "alice", "Alice"), CancellationToken.None);

        result.Error!.Type.ShouldBe(ErrorType.Conflict);
    }
}
