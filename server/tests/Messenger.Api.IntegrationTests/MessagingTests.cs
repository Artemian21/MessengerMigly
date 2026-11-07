using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class MessagingTests(ApiFactory factory)
{
    [Fact]
    public async Task Direct_chat_is_created_by_first_message_and_history_survives_in_order()
    {
        var alice = await TestUsers.CreateAsync(factory, "alice");
        var bob = await TestUsers.CreateAsync(factory, "bob");

        (await alice.Client.GetAsync($"/api/v1/conversations/direct/{bob.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var first = await alice.SendDirectAsync(bob.Id, "Hey, are you ready for class?");
        var conversationId = first.ConversationId;
        await bob.SendAsync(conversationId, "On my way");
        await alice.SendAsync(conversationId, "Grab me a seat near the window");

        var page = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{conversationId}/messages");
        page.Items.Select(m => m.Body).ShouldBe(["Hey, are you ready for class?", "On my way", "Grab me a seat near the window"]);
        page.Items.Select(m => m.Sequence).ShouldBe([1L, 2L, 3L]);
        page.Items.ShouldAllBe(m => m.CreatedAt.Offset == TimeSpan.Zero);

        var direct = await bob.GetJsonAsync<ConversationDto>($"/api/v1/conversations/direct/{alice.Id}");
        direct.Id.ShouldBe(conversationId);
        direct.UnreadCount.ShouldBe(2);
    }

    [Fact]
    public async Task Self_chat_is_rejected()
    {
        var alice = await TestUsers.CreateAsync(factory, "self");

        var response = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{alice.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), "talking to myself", null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("bad\u0007bell")]
    public async Task Invalid_messages_are_422_with_body_error(string body)
    {
        var alice = await TestUsers.CreateAsync(factory, "val");
        var bob = await TestUsers.CreateAsync(factory, "val");

        var response = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{bob.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), body, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").GetProperty("body").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Message_over_4000_characters_is_422()
    {
        var alice = await TestUsers.CreateAsync(factory, "long");
        var bob = await TestUsers.CreateAsync(factory, "long");

        var response = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{bob.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), new string('x', 4001), null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Send_is_idempotent_by_client_message_id()
    {
        var alice = await TestUsers.CreateAsync(factory, "idem");
        var bob = await TestUsers.CreateAsync(factory, "idem");
        var clientId = Guid.NewGuid();
        var request = new SendMessageRequest(clientId, "Only once please", null, null);

        var first = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{bob.Id}/messages", request);
        var retry = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{bob.Id}/messages", request);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var a = (await first.Content.ReadFromJsonAsync<MessageDto>(TestUsers.Json))!;
        var b = (await retry.Content.ReadFromJsonAsync<MessageDto>(TestUsers.Json))!;
        b.Id.ShouldBe(a.Id);
        var page = await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{a.ConversationId}/messages");
        page.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_sends_get_unique_monotonic_sequences()
    {
        var alice = await TestUsers.CreateAsync(factory, "conc");
        var bob = await TestUsers.CreateAsync(factory, "conc");
        var conversationId = (await alice.SendDirectAsync(bob.Id, "start")).ConversationId;

        var sends = Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? alice : bob).SendAsync(conversationId, $"message {i}"));
        var results = await Task.WhenAll(sends);

        results.Select(r => r.Sequence).Distinct().Count().ShouldBe(20);
        var page = await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{conversationId}/messages?limit=50");
        page.Items.Select(m => m.Sequence).ShouldBe(Enumerable.Range(1, 21).Select(i => (long)i));
    }

    [Fact]
    public async Task Concurrent_first_messages_create_exactly_one_direct_chat()
    {
        var alice = await TestUsers.CreateAsync(factory, "race");
        var bob = await TestUsers.CreateAsync(factory, "race");

        var results = await Task.WhenAll(
            alice.SendDirectAsync(bob.Id, "hi from alice"),
            bob.SendDirectAsync(alice.Id, "hi from bob"),
            alice.SendDirectAsync(bob.Id, "hi again"));

        results.Select(r => r.ConversationId).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Cursor_pagination_loads_older_pages()
    {
        var alice = await TestUsers.CreateAsync(factory, "page");
        var bob = await TestUsers.CreateAsync(factory, "page");
        var conversationId = (await alice.SendDirectAsync(bob.Id, "m1")).ConversationId;
        for (var i = 2; i <= 7; i++)
        {
            await alice.SendAsync(conversationId, $"m{i}");
        }

        var newest = await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{conversationId}/messages?limit=3");
        newest.Items.Select(m => m.Body).ShouldBe(["m5", "m6", "m7"]);
        newest.HasMore.ShouldBeTrue();

        var older = await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{conversationId}/messages?limit=3&before=5");
        older.Items.Select(m => m.Body).ShouldBe(["m2", "m3", "m4"]);

        (await alice.Client.GetAsync($"/api/v1/conversations/{conversationId}/messages?limit=500")).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Edit_history_and_info_are_author_only_and_edits_appear_in_resync()
    {
        var alice = await TestUsers.CreateAsync(factory, "edit");
        var bob = await TestUsers.CreateAsync(factory, "edit");
        var message = await alice.SendDirectAsync(bob.Id, "See you at 9");
        var lastKnown = message.Sequence;

        var edit = await alice.Client.PatchAsJsonAsync($"/api/v1/messages/{message.Id}", new EditMessageRequest("See you at 10"));
        edit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var edited = (await edit.Content.ReadFromJsonAsync<MessageDto>(TestUsers.Json))!;
        edited.IsEdited.ShouldBeTrue();
        edited.UpdatedSequence.ShouldBeGreaterThan(edited.Sequence);

        (await bob.Client.PatchAsJsonAsync($"/api/v1/messages/{message.Id}", new EditMessageRequest("hijack")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await bob.Client.GetAsync($"/api/v1/messages/{message.Id}/history")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await bob.Client.GetAsync($"/api/v1/messages/{message.Id}/info")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var history = await alice.GetJsonAsync<List<EditHistoryEntryDto>>($"/api/v1/messages/{message.Id}/history");
        history.Single().PreviousBody.ShouldBe("See you at 9");

        var sync = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{message.ConversationId}/messages?after={lastKnown}");
        sync.Items.Single().Body.ShouldBe("See you at 10");
    }

    [Fact]
    public async Task Delete_is_a_tombstone_and_replies_survive()
    {
        var alice = await TestUsers.CreateAsync(factory, "tomb");
        var bob = await TestUsers.CreateAsync(factory, "tomb");
        var original = await alice.SendDirectAsync(bob.Id, "Secret plan: pizza at 8");
        var reply = await bob.SendAsync(original.ConversationId, "I'm in!", original.Id);
        reply.ReplyTo!.BodyPreview.ShouldBe("Secret plan: pizza at 8");

        (await bob.Client.DeleteAsync($"/api/v1/messages/{original.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await alice.Client.DeleteAsync($"/api/v1/messages/{original.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var page = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{original.ConversationId}/messages");
        var tomb = page.Items.Single(m => m.Id == original.Id);
        tomb.IsDeleted.ShouldBeTrue();
        tomb.Body.ShouldBe("[Message deleted]");
        tomb.Sequence.ShouldBe(original.Sequence);
        var survivingReply = page.Items.Single(m => m.Id == reply.Id);
        survivingReply.ReplyTo!.IsDeleted.ShouldBeTrue();
        survivingReply.ReplyTo.BodyPreview.ShouldBe("[Message deleted]");

        (await alice.Client.PatchAsJsonAsync($"/api/v1/messages/{original.Id}", new EditMessageRequest("undo")))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Read_marks_update_unread_counts_and_author_status()
    {
        var alice = await TestUsers.CreateAsync(factory, "read");
        var bob = await TestUsers.CreateAsync(factory, "read");
        var m1 = await alice.SendDirectAsync(bob.Id, "one");
        var m2 = await alice.SendAsync(m1.ConversationId, "two");

        var list = await bob.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations");
        list.Single(c => c.Id == m1.ConversationId).UnreadCount.ShouldBe(2);

        (await bob.Client.PostAsJsonAsync("/api/v1/messages/delivered", new AckDeliveredRequest([m1.Id, m2.Id]))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var statuses = await alice.GetJsonAsync<List<MessageStatusDto>>($"/api/v1/conversations/{m1.ConversationId}/statuses?fromSequence=0");
        statuses.ShouldAllBe(s => s.Status == "Delivered");

        (await bob.Client.PostAsJsonAsync($"/api/v1/conversations/{m1.ConversationId}/read", new MarkReadRequest(m2.Sequence)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        list = await bob.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations");
        list.Single(c => c.Id == m1.ConversationId).UnreadCount.ShouldBe(0);

        var info = await alice.GetJsonAsync<MessageInfoDto>($"/api/v1/messages/{m2.Id}/info");
        info.Status.ShouldBe("Read");
        info.Recipients.Single().ReadAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Clearing_a_personal_chat_hides_it_until_a_new_message_arrives()
    {
        var alice = await TestUsers.CreateAsync(factory, "clear");
        var bob = await TestUsers.CreateAsync(factory, "clear");
        var first = await alice.SendDirectAsync(bob.Id, "old news");

        (await bob.Client.PostAsync($"/api/v1/conversations/{first.ConversationId}/clear", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bob.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations")).ShouldNotContain(c => c.Id == first.ConversationId);
        (await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{first.ConversationId}/messages")).Items.Count.ShouldBe(1);

        await alice.SendAsync(first.ConversationId, "fresh news");
        (await bob.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations")).ShouldContain(c => c.Id == first.ConversationId);
        var bobView = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{first.ConversationId}/messages");
        bobView.Items.Select(m => m.Body).ShouldBe(["fresh news"]);
    }

    [Fact]
    public async Task Attachment_metadata_is_stored_with_the_message()
    {
        var alice = await TestUsers.CreateAsync(factory, "attach");
        var bob = await TestUsers.CreateAsync(factory, "attach");

        var response = await alice.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{bob.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), "Lab report attached", null,
                [new Application.Messages.AttachmentInput("report.pdf", 48213, "application/pdf")]));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var message = (await response.Content.ReadFromJsonAsync<MessageDto>(TestUsers.Json))!;
        message.Attachments.Single().FileName.ShouldBe("report.pdf");
    }
}
