using System.Net;
using System.Text;
using System.Text.Json;
using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;
using Messenger.Infrastructure.Summaries;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Messenger.Api.IntegrationTests.Summaries;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Plain unit tests for the in-memory pieces; they live here because this project already references every layer.</summary>
public sealed class InMemorySummaryJobStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeClock _clock = new(Start);
    private readonly InMemorySummaryJobStore _store;

    public InMemorySummaryJobStoreTests()
    {
        _store = new InMemorySummaryJobStore(_clock, new AiOptions { JobTtlMinutes = 15 });
    }

    private static SummaryJob NewJob(Guid? user = null, Guid? conversation = null) => new(
        Guid.NewGuid(), user ?? Guid.NewGuid(), conversation ?? Guid.NewGuid(), "en", SummaryStatus.Pending,
        [new SummaryMessage("Alice", Start, "secret text")], 1, false, 1, 1);

    [Fact]
    public void A_second_request_for_the_same_user_and_chat_gets_the_pending_job()
    {
        var first = NewJob();
        var second = NewJob(first.UserId, first.ConversationId);

        _store.GetOrCreateActive(first).ShouldBe((first, true));
        var (job, created) = _store.GetOrCreateActive(second);

        created.ShouldBeFalse();
        job.Id.ShouldBe(first.Id);
        _store.Get(second.Id).ShouldBeNull();
        _store.FindActive(first.UserId, first.ConversationId)!.Id.ShouldBe(first.Id);
    }

    [Fact]
    public void Other_users_and_other_chats_do_not_share_jobs()
    {
        var job = NewJob();

        _store.GetOrCreateActive(job);

        _store.FindActive(Guid.NewGuid(), job.ConversationId).ShouldBeNull();
        _store.FindActive(job.UserId, Guid.NewGuid()).ShouldBeNull();
    }

    [Fact]
    public void Completing_a_job_stores_the_summary_drops_the_texts_and_frees_the_slot()
    {
        var job = NewJob();
        _store.GetOrCreateActive(job);

        _store.Complete(job.Id, "- done");

        var stored = _store.Get(job.Id)!;
        stored.Status.ShouldBe(SummaryStatus.Ready);
        stored.Summary.ShouldBe("- done");
        stored.Messages.ShouldBeEmpty();
        _store.FindActive(job.UserId, job.ConversationId).ShouldBeNull();
        _store.GetOrCreateActive(NewJob(job.UserId, job.ConversationId)).Created.ShouldBeTrue();
    }

    [Fact]
    public void Failing_a_job_keeps_the_error_code_and_drops_the_texts()
    {
        var job = NewJob();
        _store.GetOrCreateActive(job);

        _store.Fail(job.Id, SummaryErrorCodes.Timeout);

        var stored = _store.Get(job.Id)!;
        stored.Status.ShouldBe(SummaryStatus.Failed);
        stored.ErrorCode.ShouldBe(SummaryErrorCodes.Timeout);
        stored.Messages.ShouldBeEmpty();
    }

    [Fact]
    public void Jobs_expire_after_the_ttl_counted_from_their_last_change()
    {
        var job = NewJob();
        _store.GetOrCreateActive(job);

        _clock.UtcNow = Start.AddMinutes(14);
        _store.Get(job.Id).ShouldNotBeNull();
        _store.Complete(job.Id, "- done");

        _clock.UtcNow = Start.AddMinutes(28); // 14 min after completion: still there
        _store.Get(job.Id).ShouldNotBeNull();

        _clock.UtcNow = Start.AddMinutes(30); // 16 min after completion: gone
        _store.Get(job.Id).ShouldBeNull();
    }

    [Fact]
    public void An_expired_pending_job_no_longer_blocks_a_new_one()
    {
        var job = NewJob();
        _store.GetOrCreateActive(job);

        _clock.UtcNow = Start.AddMinutes(16);

        _store.GetOrCreateActive(NewJob(job.UserId, job.ConversationId)).Created.ShouldBeTrue();
    }
}

public sealed class SummaryJobQueueTests
{
    [Fact]
    public async Task Jobs_come_out_in_order_and_a_full_queue_rejects()
    {
        var queue = new SummaryJobQueue(new AiOptions { QueueCapacity = 2 });
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        queue.TryEnqueue(a).ShouldBeTrue();
        queue.TryEnqueue(b).ShouldBeTrue();
        queue.TryEnqueue(c).ShouldBeFalse();

        (await queue.Reader.ReadAsync()).ShouldBe(a);
        queue.TryEnqueue(c).ShouldBeTrue();
        (await queue.Reader.ReadAsync()).ShouldBe(b);
        (await queue.Reader.ReadAsync()).ShouldBe(c);
    }
}

public sealed class HttpConversationSummarizerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request);
        }
    }

    private static readonly IReadOnlyList<SummaryMessage> Messages =
        [new("Alice", new DateTimeOffset(2026, 11, 10, 17, 6, 0, TimeSpan.Zero), "Дедлайн у пʼятницю")];

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpConversationSummarizer Summarizer, StubHandler Handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new StubHandler(respond);
        var client = new HttpClient(handler);
        HttpConversationSummarizer.Configure(client, new AiOptions { BaseUrl = "http://ai.local:8000/", SharedSecret = "0123456789abcdef", TimeoutSeconds = 5 });
        return (new HttpConversationSummarizer(client, NullLogger<HttpConversationSummarizer>.Instance), handler);
    }

    [Fact]
    public async Task Posts_the_wire_contract_with_the_secret_and_returns_the_summary()
    {
        var (summarizer, handler) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"summary":"- ok","messageCount":1}""")));

        var result = await summarizer.SummarizeAsync("uk", Messages, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("- ok");
        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().ShouldBe("http://ai.local:8000/v1/summarize");
        handler.Request.Headers.GetValues("X-Ai-Secret").ShouldBe(["0123456789abcdef"]);

        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("language").GetString().ShouldBe("uk");
        var message = body.RootElement.GetProperty("messages")[0];
        message.GetProperty("author").GetString().ShouldBe("Alice");
        message.GetProperty("body").GetString().ShouldBe("Дедлайн у пʼятницю");
        message.GetProperty("sentAt").GetDateTimeOffset().ShouldBe(Messages[0].SentAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout, SummaryErrorCodes.Timeout)]
    [InlineData(HttpStatusCode.BadGateway, SummaryErrorCodes.ProviderUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized, SummaryErrorCodes.Internal)]
    [InlineData(HttpStatusCode.UnprocessableEntity, SummaryErrorCodes.Internal)]
    public async Task Error_statuses_are_mapped_to_job_error_codes(HttpStatusCode status, string code)
    {
        var (summarizer, _) = Create(_ => Task.FromResult(Json(status, """{"detail":"Alice said something private"}""")));

        var result = await summarizer.SummarizeAsync("en", Messages, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(code);
        result.Error.Message.ShouldNotContain("private");
    }

    [Fact]
    public async Task Unreachable_service_is_provider_unavailable()
    {
        var (summarizer, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var result = await summarizer.SummarizeAsync("en", Messages, CancellationToken.None);

        result.Error!.Code.ShouldBe(SummaryErrorCodes.ProviderUnavailable);
    }

    [Fact]
    public async Task Client_timeout_is_a_timeout_but_caller_cancellation_propagates()
    {
        var (timingOut, _) = Create(_ => throw new TaskCanceledException("timed out", new TimeoutException()));
        (await timingOut.SummarizeAsync("en", Messages, CancellationToken.None)).Error!.Code.ShouldBe(SummaryErrorCodes.Timeout);

        using var cts = new CancellationTokenSource();
        var (cancelled, _) = Create(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled.SummarizeAsync("en", Messages, cts.Token));
    }

    [Theory]
    [InlineData("""{"summary":""}""")]
    [InlineData("""{"other":1}""")]
    [InlineData("not json")]
    public async Task Empty_or_unreadable_answers_are_internal_errors(string json)
    {
        var (summarizer, _) = Create(_ => Task.FromResult(Json(HttpStatusCode.OK, json)));

        var result = await summarizer.SummarizeAsync("en", Messages, CancellationToken.None);

        result.Error!.Code.ShouldBe(SummaryErrorCodes.Internal);
    }
}
