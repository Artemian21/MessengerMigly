using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Messenger.Application.Users;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class RealtimeTests(ApiFactory factory)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private async Task<(HubConnection Connection, Channel<(string Event, JsonElement Payload)> Events)> ConnectAsync(TestUser user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(user.AccessToken);
            })
            .Build();
        var events = Channel.CreateUnbounded<(string, JsonElement)>();
        foreach (var name in new[] { "MessageReceived", "MessageUpdated", "MessageDeleted", "MessageStatusChanged", "Typing", "PresenceChanged", "ConversationUpdated", "ConversationRead" })
        {
            connection.On<JsonElement>(name, payload => events.Writer.TryWrite((name, payload)));
        }

        await connection.StartAsync();
        return (connection, events);
    }

    private static async Task<JsonElement> NextAsync(Channel<(string Event, JsonElement Payload)> events, string name, Func<JsonElement, bool>? match = null)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (true)
        {
            var (eventName, payload) = await events.Reader.ReadAsync(cts.Token);
            if (eventName == name && (match is null || match(payload)))
            {
                return payload;
            }
        }
    }

    [Fact]
    public async Task Send_receive_ack_read_produces_status_changes_for_the_author()
    {
        var alice = await TestUsers.CreateAsync(factory, "rta");
        var bob = await TestUsers.CreateAsync(factory, "rtb");
        var (aliceHub, aliceEvents) = await ConnectAsync(alice);
        var (bobHub, bobEvents) = await ConnectAsync(bob);

        var sent = await alice.SendDirectAsync(bob.Id, "live hello");

        var received = await NextAsync(bobEvents, "MessageReceived");
        received.GetProperty("id").GetGuid().ShouldBe(sent.Id);
        received.GetProperty("body").GetString().ShouldBe("live hello");

        (await bobHub.InvokeAsync<int>("AckDelivered", new[] { sent.Id })).ShouldBe(1);
        (await bobHub.InvokeAsync<int>("AckDelivered", new[] { sent.Id })).ShouldBe(0);
        var delivered = await NextAsync(aliceEvents, "MessageStatusChanged");
        delivered.GetProperty("status").GetString().ShouldBe("Delivered");

        await bobHub.InvokeAsync<long>("MarkRead", sent.ConversationId, sent.Sequence);
        var read = await NextAsync(aliceEvents, "MessageStatusChanged", p => p.GetProperty("status").GetString() == "Read");
        read.GetProperty("messageId").GetGuid().ShouldBe(sent.Id);

        await aliceHub.DisposeAsync();
        await bobHub.DisposeAsync();
    }

    [Fact]
    public async Task Typing_reaches_others_who_opened_the_conversation_and_outsiders_cannot_open_it()
    {
        var alice = await TestUsers.CreateAsync(factory, "typa");
        var bob = await TestUsers.CreateAsync(factory, "typb");
        var eve = await TestUsers.CreateAsync(factory, "type");
        var conversationId = (await alice.SendDirectAsync(bob.Id, "typing test")).ConversationId;
        var (aliceHub, _) = await ConnectAsync(alice);
        var (bobHub, bobEvents) = await ConnectAsync(bob);
        var (eveHub, _) = await ConnectAsync(eve);

        await bobHub.InvokeAsync("OpenConversation", conversationId);
        await aliceHub.InvokeAsync("Typing", conversationId);

        var typing = await NextAsync(bobEvents, "Typing");
        typing.GetProperty("userId").GetGuid().ShouldBe(alice.Id);
        await Should.ThrowAsync<HubException>(() => eveHub.InvokeAsync("OpenConversation", conversationId));

        await aliceHub.DisposeAsync();
        await bobHub.DisposeAsync();
        await eveHub.DisposeAsync();
    }

    [Fact]
    public async Task Presence_goes_offline_only_after_the_grace_period_and_only_for_contacts()
    {
        var alice = await TestUsers.CreateAsync(factory, "pra");
        var bob = await TestUsers.CreateAsync(factory, "prb");
        var stranger = await TestUsers.CreateAsync(factory, "prs");
        await alice.SendDirectAsync(bob.Id, "presence");
        var (bobHub, bobEvents) = await ConnectAsync(bob);
        var (aliceHub, _) = await ConnectAsync(alice);
        var (strangerHub, _) = await ConnectAsync(stranger);

        var snapshot = await bobHub.InvokeAsync<List<PresenceDto>>("WatchPresence", new[] { alice.Id });
        snapshot.Single().Online.ShouldBeTrue();
        (await strangerHub.InvokeAsync<List<PresenceDto>>("WatchPresence", new[] { alice.Id })).ShouldBeEmpty();

        await aliceHub.DisposeAsync();
        var offline = await NextAsync(bobEvents, "PresenceChanged", p => !p.GetProperty("online").GetBoolean());
        offline.GetProperty("userId").GetGuid().ShouldBe(alice.Id);
        offline.GetProperty("lastSeenAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));

        var profile = await bob.GetJsonAsync<PublicUserDto>($"/api/v1/users/{alice.Id}");
        profile.Online.ShouldBe(false);
        profile.LastSeenAt.ShouldNotBeNull();

        await bobHub.DisposeAsync();
        await strangerHub.DisposeAsync();
    }

    [Fact]
    public async Task Missed_messages_are_recovered_through_rest_resync_after_reconnect()
    {
        var alice = await TestUsers.CreateAsync(factory, "rsa");
        var bob = await TestUsers.CreateAsync(factory, "rsb");
        var first = await alice.SendDirectAsync(bob.Id, "before disconnect");
        var (bobHub, _) = await ConnectAsync(bob);
        await bobHub.StopAsync();

        var missed = await alice.SendAsync(first.ConversationId, "while you were away");
        await alice.Client.PatchAsJsonAsync($"/api/v1/messages/{first.Id}", new Api.Contracts.EditMessageRequest("before disconnect (edited)"));

        await bobHub.StartAsync();
        var sync = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{first.ConversationId}/messages?after={first.Sequence}");
        sync.Items.Select(m => m.Id).ShouldBe([first.Id, missed.Id], ignoreOrder: true);
        sync.Items.Single(m => m.Id == first.Id).Body.ShouldBe("before disconnect (edited)");

        (await bobHub.InvokeAsync<int>("AckDelivered", sync.Items.Select(m => m.Id).ToArray())).ShouldBe(2);
        await bobHub.DisposeAsync();
    }

    [Fact]
    public async Task Hub_rejects_anonymous_connections()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync());
    }
}
