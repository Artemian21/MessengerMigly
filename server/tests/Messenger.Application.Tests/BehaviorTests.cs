using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Admin;
using Messenger.Application.Common;
using Messenger.Application.Messages;
using Messenger.Application.Users;
using Messenger.Domain.Common;
using NSubstitute;
using Shouldly;

namespace Messenger.Application.Tests;

public sealed class BehaviorTests
{
    [Fact]
    public async Task Validation_failure_returns_validation_error_with_camel_case_fields()
    {
        var behavior = new ValidationBehavior<SendMessageCommand, Result<SendMessageResult>>([new SendMessageValidator()]);
        var command = new SendMessageCommand(Guid.NewGuid(), Guid.NewGuid(), "   ", null, null);

        var result = await behavior.Handle(command, () => throw new InvalidOperationException("handler must not run"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Type.ShouldBe(ErrorType.Validation);
        result.Error.Fields!.Keys.ShouldContain("body");
    }

    [Fact]
    public async Task Too_long_message_is_rejected()
    {
        var behavior = new ValidationBehavior<SendMessageCommand, Result<SendMessageResult>>([new SendMessageValidator()]);
        var command = new SendMessageCommand(Guid.NewGuid(), Guid.NewGuid(), new string('x', 4001), null, null);

        var result = await behavior.Handle(command, () => throw new InvalidOperationException(), CancellationToken.None);

        result.Error!.Fields!["body"].ShouldContain(m => m.Contains("4000"));
    }

    [Fact]
    public async Task Anonymous_caller_is_unauthorized()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns((Guid?)null);
        var behavior = new AuthorizationBehavior<GetMeQuery, Result<MeDto>>(currentUser, Substitute.For<IApplicationDbContext>());

        var result = await behavior.Handle(new GetMeQuery(), () => throw new InvalidOperationException(), CancellationToken.None);

        result.Error!.Type.ShouldBe(ErrorType.Unauthorized);
    }

    [Fact]
    public async Task Non_admin_cannot_use_admin_requests()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.IsAdmin.Returns(false);
        var behavior = new AuthorizationBehavior<AdminListUsersQuery, Result<PagedResult<AdminUserDto>>>(currentUser, Substitute.For<IApplicationDbContext>());

        var result = await behavior.Handle(new AdminListUsersQuery(null, null, null), () => throw new InvalidOperationException(), CancellationToken.None);

        result.Error!.Type.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public async Task Transaction_behavior_skips_queries()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var behavior = new TransactionBehavior<GetMeQuery, Result<MeDto>>(unitOfWork);
        var called = false;

        await behavior.Handle(new GetMeQuery(), () => { called = true; return Task.FromResult<Result<MeDto>>(Error.NotFound("x")); }, CancellationToken.None);

        called.ShouldBeTrue();
        await unitOfWork.DidNotReceiveWithAnyArgs().ExecuteInTransactionAsync<Result<MeDto>>(default!, default!, default);
    }

    [Fact]
    public async Task Transaction_behavior_wraps_commands()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<Func<Result, bool>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result>>>()(CancellationToken.None));
        var behavior = new TransactionBehavior<DeleteMessageCommand, Result>(unitOfWork);

        var result = await behavior.Handle(new DeleteMessageCommand(Guid.NewGuid()), () => Task.FromResult(Result.Success()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await unitOfWork.ReceivedWithAnyArgs(1).ExecuteInTransactionAsync<Result>(default!, default!, default);
    }

    [Fact]
    public void Paging_clamps_limits()
    {
        Paging.Limit(500).ShouldBe(Paging.MaxLimit);
        Paging.Limit(0).ShouldBe(1);
        Paging.Offset(-5).ShouldBe(0);
        PagedResult<int>.Create([1, 2], total: 5, offset: 0, limit: 2).Next.ShouldBe(2);
        PagedResult<int>.Create([5], total: 5, offset: 4, limit: 2).Next.ShouldBeNull();
    }

    [Fact]
    public void Like_patterns_escape_wildcards()
    {
        LikePattern.Contains("50%_off").ShouldBe("%50\\%\\_off%");
        LikePattern.Prefix("a\\b").ShouldBe("a\\\\b%");
    }

    [Fact]
    public void Search_snippet_centers_on_the_match()
    {
        var body = new string('a', 200) + " pizza tonight " + new string('b', 200);

        var snippet = SearchMessagesHandler.Snippet(body, "PIZZA");

        snippet.ShouldContain("pizza tonight");
        snippet.ShouldStartWith("…");
        snippet.ShouldEndWith("…");
    }
}
