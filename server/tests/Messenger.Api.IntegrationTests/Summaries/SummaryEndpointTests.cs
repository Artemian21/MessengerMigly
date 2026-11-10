using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Summaries;
using Messenger.Domain.Common;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;

namespace Messenger.Api.IntegrationTests.Summaries;

/// <summary>Whole path on a real database: REST → snapshot → background worker → (fake) AI service → GET / hub event.
/// Authors are "{prefix} tester" (see TestUsers.CreateAsync).</summary>
[Collection(ApiCollection.Name)]
public sealed class SummaryEndpointTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly ApiFactory _factory;
    private readonly FakeSummarizer _ai;

    public SummaryEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _ai = factory.Summarizer;
        _ai.Reset();
    }

    private static string Url(Guid conversationId) => $"/api/v1/conversations/{conversationId}/summary";

    private static async Task<(HttpStatusCode Status, SummaryJobDto Dto)> StartAsync(TestUser user, Guid conversationId, string? language = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(conversationId));
        if (language is not null)
        {
            request.Headers.Add("Accept-Language", language);
        }

        var response = await user.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var dto = response.IsSuccessStatusCode ? JsonSerializer.Deserialize<SummaryJobDto>(body, TestUsers.Json) : null;
        return (response.StatusCode, dto ?? new SummaryJobDto(null, "error:" + body, null, null, 0, false, 0, 0));
    }

    private static async Task<SummaryJobDto> WaitUntilFinishedAsync(TestUser user, Guid conversationId, Guid jobId)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (true)
        {
            var job = await user.GetJsonAsync<SummaryJobDto>($"{Url(conversationId)}/{jobId}");
            if (job.Status != "pending")
            {
                return job;
            }

            await Task.Delay(25, cts.Token);
        }
    }

    private IReadOnlyList<string> SummarizedBodies() => _ai.Calls.Single().Messages.Select(m => m.Body).ToList();

    [Fact]
    public async Task Unread_messages_are_summarized_in_order_without_my_own()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sa");
        var bob = await TestUsers.CreateAsync(_factory, "sb");
        var first = await alice.SendDirectAsync(bob.Id, "m1");
        await alice.SendAsync(first.ConversationId, "m2");
        await bob.SendAsync(first.ConversationId, "bob's own message");
        await alice.SendAsync(first.ConversationId, "m3");

        var (status, started) = await StartAsync(bob, first.ConversationId);

        status.ShouldBe(HttpStatusCode.Accepted);
        started.Status.ShouldBe("pending");
        started.JobId.ShouldNotBeNull();
        started.MessageCount.ShouldBe(3);
        started.Truncated.ShouldBeFalse();
        started.ToSequence.ShouldBe(started.FromSequence + 3); // m1 (1) .. m3 (4): bob's message sits in between

        var done = await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId.Value);
        done.Status.ShouldBe("ready");
        done.Summary.ShouldBe("- fake summary");
        SummarizedBodies().ShouldBe(["m1", "m2", "m3"]);
        _ai.Calls.Single().Messages.ShouldAllBe(m => m.Author == "sa tester");
    }

    [Fact]
    public async Task Nothing_unread_returns_an_empty_status_without_calling_the_ai()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sc");
        var bob = await TestUsers.CreateAsync(_factory, "sd");
        var first = await alice.SendDirectAsync(bob.Id, "hello");

        var (status, own) = await StartAsync(alice, first.ConversationId); // the sender has nothing unread

        status.ShouldBe(HttpStatusCode.OK);
        own.Status.ShouldBe("empty");
        own.JobId.ShouldBeNull();
        _ai.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("uk-UA", "uk")]
    [InlineData("de", "en")]
    [InlineData(null, "en")]
    public async Task Summary_language_follows_the_request_language(string? acceptLanguage, string expected)
    {
        var alice = await TestUsers.CreateAsync(_factory, "se");
        var bob = await TestUsers.CreateAsync(_factory, "sf");
        var first = await alice.SendDirectAsync(bob.Id, "hi");

        var (_, started) = await StartAsync(bob, first.ConversationId, acceptLanguage);
        await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId!.Value);

        _ai.Calls.Single().Language.ShouldBe(expected);
    }

    [Fact]
    public async Task Only_members_can_start_a_summary_and_only_the_owner_can_read_a_job()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sg");
        var bob = await TestUsers.CreateAsync(_factory, "sh");
        var outsider = await TestUsers.CreateAsync(_factory, "si");
        var first = await alice.SendDirectAsync(bob.Id, "private");
        var (_, started) = await StartAsync(bob, first.ConversationId);
        await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId!.Value);

        (await StartAsync(outsider, first.ConversationId)).Status.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.Client.GetAsync($"{Url(first.ConversationId)}/{started.JobId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // a member of the chat, but not the job's owner
        (await alice.Client.GetAsync($"{Url(first.ConversationId)}/{started.JobId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // the owner asking through another conversation id
        var other = await bob.SendDirectAsync(outsider.Id, "elsewhere");
        (await bob.Client.GetAsync($"{Url(other.ConversationId)}/{started.JobId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Messages_I_have_already_read_are_not_summarized()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sj");
        var bob = await TestUsers.CreateAsync(_factory, "sk");
        await alice.SendDirectAsync(bob.Id, "old 1");
        var second = await alice.SendDirectAsync(bob.Id, "old 2");
        await alice.SendAsync(second.ConversationId, "new");
        (await bob.Client.PostAsJsonAsync($"/api/v1/conversations/{second.ConversationId}/read", new MarkReadRequest(second.Sequence)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, started) = await StartAsync(bob, second.ConversationId);
        await WaitUntilFinishedAsync(bob, second.ConversationId, started.JobId!.Value);

        SummarizedBodies().ShouldBe(["new"]);
    }

    [Fact]
    public async Task Messages_before_a_cleared_history_and_deleted_messages_are_not_summarized()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sl");
        var bob = await TestUsers.CreateAsync(_factory, "sm");
        var first = await alice.SendDirectAsync(bob.Id, "before clear");
        (await bob.Client.PostAsync($"/api/v1/conversations/{first.ConversationId}/clear", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await alice.SendAsync(first.ConversationId, "kept");
        var deleted = await alice.SendAsync(first.ConversationId, "deleted later");
        await alice.SendAsync(first.ConversationId, "also kept");
        (await alice.Client.DeleteAsync($"/api/v1/messages/{deleted.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (_, started) = await StartAsync(bob, first.ConversationId);
        await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId!.Value);

        SummarizedBodies().ShouldBe(["kept", "also kept"]);
    }

    [Fact]
    public async Task A_long_backlog_is_cut_to_the_latest_messages_and_flagged()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sn");
        var bob = await TestUsers.CreateAsync(_factory, "so");
        var first = await alice.SendDirectAsync(bob.Id, "m1");
        foreach (var body in new[] { "m2", "m3", "m4", "m5", "m6" })
        {
            await alice.SendAsync(first.ConversationId, body);
        }

        var (_, started) = await StartAsync(bob, first.ConversationId); // Ai:MaxMessages is 4 in tests
        await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId!.Value);

        started.MessageCount.ShouldBe(4);
        started.Truncated.ShouldBeTrue();
        SummarizedBodies().ShouldBe(["m3", "m4", "m5", "m6"]);
    }

    [Fact]
    public async Task In_a_group_everyone_else_s_messages_are_included()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sp");
        var bob = await TestUsers.CreateAsync(_factory, "sq");
        var carol = await TestUsers.CreateAsync(_factory, "sr");
        var group = await alice.CreateGroupAsync("Room 412", bob, carol);
        await alice.SendAsync(group.Id, "from alice");
        await bob.SendAsync(group.Id, "from bob");

        var (_, started) = await StartAsync(carol, group.Id);
        await WaitUntilFinishedAsync(carol, group.Id, started.JobId!.Value);

        _ai.Calls.Single().Messages.Select(m => (m.Author, m.Body)).ShouldBe([("sp tester", "from alice"), ("sq tester", "from bob")]);
    }

    [Fact]
    public async Task A_failed_ai_call_is_reported_as_an_error_code_only()
    {
        var alice = await TestUsers.CreateAsync(_factory, "ss");
        var bob = await TestUsers.CreateAsync(_factory, "st");
        var first = await alice.SendDirectAsync(bob.Id, "secret plans");
        _ai.Next = Result<string>.Failure(new Error(ErrorType.Conflict, SummaryErrorCodes.Timeout, "contains secret plans"));

        var (_, started) = await StartAsync(bob, first.ConversationId);
        var done = await WaitUntilFinishedAsync(bob, first.ConversationId, started.JobId!.Value);

        done.Status.ShouldBe("failed");
        done.ErrorCode.ShouldBe("timeout");
        done.Summary.ShouldBeNull();
    }

    [Fact]
    public async Task A_second_request_while_one_is_running_returns_the_same_job()
    {
        var alice = await TestUsers.CreateAsync(_factory, "su");
        var bob = await TestUsers.CreateAsync(_factory, "sv");
        var first = await alice.SendDirectAsync(bob.Id, "hi");
        _ai.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var (_, one) = await StartAsync(bob, first.ConversationId);
        var (status, two) = await StartAsync(bob, first.ConversationId);

        status.ShouldBe(HttpStatusCode.Accepted);
        two.JobId.ShouldBe(one.JobId);
        two.Status.ShouldBe("pending");

        _ai.Gate.SetResult();
        await WaitUntilFinishedAsync(bob, first.ConversationId, one.JobId!.Value);
        _ai.Calls.Count.ShouldBe(1);

        var (_, again) = await StartAsync(bob, first.ConversationId); // finished jobs do not block a new one
        again.JobId.ShouldNotBe(one.JobId);
    }

    [Fact]
    public async Task The_requesters_devices_get_a_SummaryCompleted_event()
    {
        var alice = await TestUsers.CreateAsync(_factory, "sw");
        var bob = await TestUsers.CreateAsync(_factory, "sx");
        var first = await alice.SendDirectAsync(bob.Id, "hi");
        var events = Channel.CreateUnbounded<JsonElement>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(bob.AccessToken);
            })
            .Build();
        hub.On<JsonElement>("SummaryCompleted", payload => events.Writer.TryWrite(payload));
        await hub.StartAsync();

        var (_, started) = await StartAsync(bob, first.ConversationId);

        using var cts = new CancellationTokenSource(Wait);
        var payload = await events.Reader.ReadAsync(cts.Token);
        payload.GetProperty("conversationId").GetGuid().ShouldBe(first.ConversationId);
        payload.GetProperty("jobId").GetGuid().ShouldBe(started.JobId!.Value);
        payload.GetProperty("status").GetString().ShouldBe("ready");
        payload.GetProperty("summary").GetString().ShouldBe("- fake summary");
        payload.GetProperty("errorCode").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
