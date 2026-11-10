using Messenger.Application.Abstractions;
using Messenger.Application.Messages;
using Messenger.Application.Summaries;
using Messenger.Domain.Common;
using NSubstitute;
using Shouldly;

namespace Messenger.Application.Tests;

/// <summary>The paths that do not touch the database. Snapshot rules (watermarks, tombstones, truncation) are covered by
/// the integration tests, because EF queries need a real provider.</summary>
public sealed class SummaryHandlerTests
{
    private readonly ISummaryJobStore _store = Substitute.For<ISummaryJobStore>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly Guid _me = Guid.NewGuid();

    public SummaryHandlerTests()
    {
        _currentUser.UserId.Returns(_me);
        // NSubstitute also stubs the interface's default member, so it has to be set explicitly.
        _currentUser.RequiredUserId.Returns(_me);
    }

    private static SummaryJob Job(Guid user, Guid conversation, SummaryStatus status = SummaryStatus.Ready) => new(
        Guid.NewGuid(), user, conversation, "en", status, [], 3, true, 10, 12, "- done");

    private StartSummaryHandler StartHandler(bool enabled) => new(
        Substitute.For<IApplicationDbContext>(), new MessageReader(Substitute.For<IApplicationDbContext>()), _store,
        Substitute.For<ISummaryJobQueue>(), _currentUser, new AiOptions { Enabled = enabled });

    [Fact]
    public async Task Start_is_not_found_when_the_feature_is_off()
    {
        var result = await StartHandler(enabled: false).Handle(new StartSummaryQuery(Guid.NewGuid()), CancellationToken.None);

        result.Error!.Type.ShouldBe(ErrorType.NotFound);
        _store.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Start_returns_the_already_running_job_without_reading_messages()
    {
        var conversation = Guid.NewGuid();
        var running = Job(_me, conversation, SummaryStatus.Pending) with { Summary = null };
        _store.FindActive(_me, conversation).Returns(running);

        var result = await StartHandler(enabled: true).Handle(new StartSummaryQuery(conversation), CancellationToken.None);

        result.Value.JobId.ShouldBe(running.Id);
        result.Value.Status.ShouldBe("pending");
    }

    [Fact]
    public async Task Start_rejects_a_negative_AfterSequence()
    {
        var validator = new StartSummaryValidator();

        validator.Validate(new StartSummaryQuery(Guid.NewGuid(), -1)).IsValid.ShouldBeFalse();
        validator.Validate(new StartSummaryQuery(Guid.NewGuid(), 0)).IsValid.ShouldBeTrue();
        validator.Validate(new StartSummaryQuery(Guid.NewGuid())).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, 2, 2)]
    [InlineData(false, 3, 3)]
    [InlineData(true, 0, 1)] // a misconfigured minimum never drops below one message
    public async Task Config_reports_whether_summaries_are_on_and_the_minimum(bool enabled, int configured, int reported)
    {
        var handler = new GetAiConfigHandler(new AiOptions { Enabled = enabled, MinMessages = configured });

        var result = await handler.Handle(new GetAiConfigQuery(), CancellationToken.None);

        result.Value.ShouldBe(new AiConfigDto(enabled, reported));
    }

    [Fact]
    public async Task Get_returns_the_owners_job_with_its_state()
    {
        var conversation = Guid.NewGuid();
        var job = Job(_me, conversation);
        _store.Get(job.Id).Returns(job);

        var result = await new GetSummaryHandler(_store, _currentUser, new AiOptions { Enabled = true })
            .Handle(new GetSummaryQuery(conversation, job.Id), CancellationToken.None);

        result.Value.ShouldBe(new SummaryJobDto(job.Id, "ready", "- done", null, 3, true, 10, 12));
    }

    [Fact]
    public async Task Get_hides_other_users_jobs_other_chats_unknown_ids_and_a_disabled_feature()
    {
        var conversation = Guid.NewGuid();
        var othersJob = Job(Guid.NewGuid(), conversation);
        var otherChatJob = Job(_me, Guid.NewGuid());
        _store.Get(othersJob.Id).Returns(othersJob);
        _store.Get(otherChatJob.Id).Returns(otherChatJob);
        var enabled = new GetSummaryHandler(_store, _currentUser, new AiOptions { Enabled = true });

        (await enabled.Handle(new GetSummaryQuery(conversation, othersJob.Id), CancellationToken.None)).Error!.Type.ShouldBe(ErrorType.NotFound);
        (await enabled.Handle(new GetSummaryQuery(conversation, otherChatJob.Id), CancellationToken.None)).Error!.Type.ShouldBe(ErrorType.NotFound);
        (await enabled.Handle(new GetSummaryQuery(conversation, Guid.NewGuid()), CancellationToken.None)).Error!.Type.ShouldBe(ErrorType.NotFound);

        var disabled = new GetSummaryHandler(_store, _currentUser, new AiOptions { Enabled = false });
        (await disabled.Handle(new GetSummaryQuery(otherChatJob.ConversationId, otherChatJob.Id), CancellationToken.None))
            .Error!.Type.ShouldBe(ErrorType.NotFound);
    }
}
