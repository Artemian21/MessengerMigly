using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;
using Messenger.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Messenger.Application.Tests;

public sealed class SummaryJobProcessorTests
{
    private readonly ISummaryJobStore _store = Substitute.For<ISummaryJobStore>();
    private readonly IConversationSummarizer _summarizer = Substitute.For<IConversationSummarizer>();
    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly SummaryJobProcessor _processor;

    public SummaryJobProcessorTests()
    {
        _processor = new SummaryJobProcessor(_store, _summarizer, _notifier, NullLogger<SummaryJobProcessor>.Instance);
    }

    private static SummaryJob Job(SummaryStatus status = SummaryStatus.Pending) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "uk", status,
        [new SummaryMessage("Alice", DateTimeOffset.UtcNow, "hi")], 1, false, 5, 5);

    private static Error Failure(string code) => new(ErrorType.Conflict, code, "AI service failed");

    private void Summarizer(Result<string> result) =>
        _summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SummaryMessage>>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Fact]
    public async Task Successful_job_is_completed_and_the_requester_is_notified()
    {
        var job = Job();
        _store.Get(job.Id).Returns(job);
        Summarizer(Result.Success("- decided"));

        await _processor.ProcessAsync(job.Id, CancellationToken.None);

        await _summarizer.Received(1).SummarizeAsync("uk", job.Messages, Arg.Any<CancellationToken>());
        _store.Received(1).Complete(job.Id, "- decided");
        await _notifier.Received(1).SummaryCompletedAsync(
            job.UserId, job.ConversationId, job.Id, "ready", "- decided", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_summarizer_marks_the_job_failed_with_its_error_code()
    {
        var job = Job();
        _store.Get(job.Id).Returns(job);
        Summarizer(Result<string>.Failure(Failure(SummaryErrorCodes.Timeout)));

        await _processor.ProcessAsync(job.Id, CancellationToken.None);

        _store.Received(1).Fail(job.Id, SummaryErrorCodes.Timeout);
        _store.DidNotReceive().Complete(Arg.Any<Guid>(), Arg.Any<string>());
        await _notifier.Received(1).SummaryCompletedAsync(
            job.UserId, job.ConversationId, job.Id, "failed", null, SummaryErrorCodes.Timeout, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unexpected_exception_becomes_an_internal_failure()
    {
        var job = Job();
        _store.Get(job.Id).Returns(job);
        _summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SummaryMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<string>>>(_ => throw new InvalidOperationException("boom"));

        await _processor.ProcessAsync(job.Id, CancellationToken.None);

        _store.Received(1).Fail(job.Id, SummaryErrorCodes.Internal);
    }

    [Fact]
    public async Task Unknown_or_finished_jobs_are_ignored()
    {
        var unknown = Guid.NewGuid();
        _store.Get(unknown).Returns((SummaryJob?)null);
        var finished = Job(SummaryStatus.Ready);
        _store.Get(finished.Id).Returns(finished);

        await _processor.ProcessAsync(unknown, CancellationToken.None);
        await _processor.ProcessAsync(finished.Id, CancellationToken.None);

        await _summarizer.DidNotReceiveWithAnyArgs().SummarizeAsync(default!, default!, default);
    }

    [Fact]
    public async Task Notification_failure_does_not_undo_the_result()
    {
        var job = Job();
        _store.Get(job.Id).Returns(job);
        Summarizer(Result.Success("- ok"));
        _notifier.SummaryCompletedAsync(default, default, default, default!, default, default, default)
            .ReturnsForAnyArgs<Task>(_ => throw new InvalidOperationException("hub down"));

        await Should.NotThrowAsync(() => _processor.ProcessAsync(job.Id, CancellationToken.None));

        _store.Received(1).Complete(job.Id, "- ok");
    }

    [Fact]
    public async Task Cancellation_during_shutdown_leaves_the_job_alone()
    {
        var job = Job();
        _store.Get(job.Id).Returns(job);
        using var cts = new CancellationTokenSource();
        _summarizer.SummarizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<SummaryMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<string>>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        await _processor.ProcessAsync(job.Id, cts.Token);

        _store.DidNotReceiveWithAnyArgs().Fail(default, default!);
        await _notifier.DidNotReceiveWithAnyArgs().SummaryCompletedAsync(default, default, default, default!, default, default, default);
    }
}
