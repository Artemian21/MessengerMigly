using System.Net;
using System.Net.Http.Json;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class GroupsSearchAndBolaTests(ApiFactory factory)
{
    [Fact]
    public async Task Group_needs_three_members_and_owner_manages_it()
    {
        var owner = await TestUsers.CreateAsync(factory, "owner");
        var bob = await TestUsers.CreateAsync(factory, "gbob");
        var carol = await TestUsers.CreateAsync(factory, "gcarol");

        var tooSmall = await owner.Client.PostAsJsonAsync("/api/v1/conversations/groups", new CreateGroupRequest("Duo", [bob.Id]));
        tooSmall.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var group = await owner.CreateGroupAsync("Room 412", bob, carol);
        group.MyRole.ShouldBe("Owner");
        group.MemberCount.ShouldBe(3);

        (await bob.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}", new RenameGroupRequest("Hacked"))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}/members/{bob.Id}", new ChangeRoleRequest("Admin")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await bob.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}", new RenameGroupRequest("Room 412 · Exams"))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await bob.Client.DeleteAsync($"/api/v1/conversations/{group.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task New_member_sees_only_messages_after_joining()
    {
        var owner = await TestUsers.CreateAsync(factory, "join");
        var bob = await TestUsers.CreateAsync(factory, "join");
        var carol = await TestUsers.CreateAsync(factory, "join");
        var dave = await TestUsers.CreateAsync(factory, "join");
        var group = await owner.CreateGroupAsync("Before and after", bob, carol);
        await owner.SendAsync(group.Id, "before dave joined");

        (await owner.Client.PostAsJsonAsync($"/api/v1/conversations/{group.Id}/members", new AddMembersRequest([dave.Id])))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await bob.SendAsync(group.Id, "welcome dave");

        var page = await dave.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{group.Id}/messages");
        page.Items.Select(m => m.Body).ShouldBe(["welcome dave"]);
    }

    [Fact]
    public async Task Owner_leaving_transfers_ownership_and_deleted_group_rejects_messages()
    {
        var owner = await TestUsers.CreateAsync(factory, "leave");
        var bob = await TestUsers.CreateAsync(factory, "leave");
        var carol = await TestUsers.CreateAsync(factory, "leave");
        var group = await owner.CreateGroupAsync("Leaving", bob, carol);
        await owner.Client.PatchAsJsonAsync($"/api/v1/conversations/{group.Id}/members/{carol.Id}", new ChangeRoleRequest("Admin"));

        (await owner.Client.PostAsync($"/api/v1/conversations/{group.Id}/leave", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.Client.GetAsync($"/api/v1/conversations/{group.Id}/messages")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var members = await bob.GetJsonAsync<List<MemberDto>>($"/api/v1/conversations/{group.Id}/members");
        members.Single(m => m.UserId == carol.Id).Role.ShouldBe("Owner");

        (await carol.Client.DeleteAsync($"/api/v1/conversations/{group.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var send = await bob.Client.PostAsJsonAsync($"/api/v1/conversations/{group.Id}/messages",
            new SendMessageRequest(Guid.NewGuid(), "anyone here?", null, null));
        send.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await bob.Client.GetAsync($"/api/v1/conversations/{group.Id}/messages")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bola_outsider_cannot_touch_someone_elses_conversation()
    {
        var bob = await TestUsers.CreateAsync(factory, "bola");
        var carol = await TestUsers.CreateAsync(factory, "bola");
        var alice = await TestUsers.CreateAsync(factory, "bola");
        var message = await bob.SendDirectAsync(carol.Id, "private between bob and carol: unicorn");
        var id = message.ConversationId;

        (await alice.Client.GetAsync($"/api/v1/conversations/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.GetAsync($"/api/v1/conversations/{id}/messages")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.GetAsync($"/api/v1/conversations/{id}/members")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.GetAsync($"/api/v1/conversations/{id}/statuses?fromSequence=0")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.PostAsJsonAsync($"/api/v1/conversations/{id}/messages", new SendMessageRequest(Guid.NewGuid(), "intrude", null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.PostAsJsonAsync($"/api/v1/conversations/{id}/read", new MarkReadRequest(100))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.PatchAsJsonAsync($"/api/v1/messages/{message.Id}", new EditMessageRequest("hijack"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.DeleteAsync($"/api/v1/messages/{message.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.GetAsync($"/api/v1/messages/{message.Id}/history")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await alice.Client.GetAsync($"/api/v1/messages/{message.Id}/info")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var ack = await alice.Client.PostAsJsonAsync("/api/v1/messages/delivered", new AckDeliveredRequest([message.Id]));
        (await ack.Content.ReadFromJsonAsync<AckDeliveredResponse>())!.Acknowledged.ShouldBe(0);

        var search = await alice.GetJsonAsync<PagedResult<SearchHitDto>>("/api/v1/search/messages?q=unicorn");
        search.Total.ShouldBe(0);
        var scoped = await alice.GetJsonAsync<PagedResult<SearchHitDto>>($"/api/v1/search/messages?q=unicorn&conversationId={id}");
        scoped.Total.ShouldBe(0);

        var bobInfo = await bob.GetJsonAsync<MessageInfoDto>($"/api/v1/messages/{message.Id}/info");
        bobInfo.Status.ShouldBe("Sent");
    }

    [Fact]
    public async Task Search_respects_join_clear_and_delete_visibility()
    {
        var owner = await TestUsers.CreateAsync(factory, "srch");
        var bob = await TestUsers.CreateAsync(factory, "srch");
        var carol = await TestUsers.CreateAsync(factory, "srch");
        var dave = await TestUsers.CreateAsync(factory, "srch");
        var token = Guid.NewGuid().ToString("N")[..10];
        var group = await owner.CreateGroupAsync("Search crew", bob, carol);

        await owner.SendAsync(group.Id, $"before join {token}");
        await owner.Client.PostAsJsonAsync($"/api/v1/conversations/{group.Id}/members", new AddMembersRequest([dave.Id]));
        var visible = await owner.SendAsync(group.Id, $"after join {token.ToUpperInvariant()}");
        var deleted = await owner.SendAsync(group.Id, $"to be deleted {token}");
        await owner.Client.DeleteAsync($"/api/v1/messages/{deleted.Id}");

        var daveHits = await dave.GetJsonAsync<PagedResult<SearchHitDto>>($"/api/v1/search/messages?q={token}");
        daveHits.Items.Select(h => h.MessageId).ShouldBe([visible.Id]);

        var bobHits = await bob.GetJsonAsync<PagedResult<SearchHitDto>>($"/api/v1/search/messages?q={token}");
        bobHits.Total.ShouldBe(2);

        var direct = await owner.SendDirectAsync(carol.Id, $"direct before clear {token}");
        await carol.Client.PostAsync($"/api/v1/conversations/{direct.ConversationId}/clear", null);
        var carolHits = await carol.GetJsonAsync<PagedResult<SearchHitDto>>($"/api/v1/search/messages?q={token}");
        carolHits.Items.ShouldNotContain(h => h.MessageId == direct.Id);

        (await bob.Client.GetAsync("/api/v1/search/messages?q=a")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Search_treats_wildcards_literally()
    {
        var alice = await TestUsers.CreateAsync(factory, "wild");
        var bob = await TestUsers.CreateAsync(factory, "wild");
        await alice.SendDirectAsync(bob.Id, "plain text without specials");

        var hits = await alice.GetJsonAsync<PagedResult<SearchHitDto>>("/api/v1/search/messages?q=%25%25");
        hits.Total.ShouldBe(0);
    }

    [Fact]
    public async Task User_search_finds_by_prefix_and_paginates()
    {
        var alice = await TestUsers.CreateAsync(factory, "finder");
        var target = await TestUsers.CreateAsync(factory, "zzfind");

        var result = await alice.GetJsonAsync<PagedResult<UserSummaryDto>>($"/api/v1/users?q={target.Username[..8]}&limit=5");

        result.Items.ShouldContain(u => u.Id == target.Id);
        result.Items.ShouldNotContain(u => u.Id == alice.Id);
    }
}
