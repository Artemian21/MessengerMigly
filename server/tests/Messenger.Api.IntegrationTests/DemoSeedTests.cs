using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Messenger.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

/// <summary>The demo data (`seed` command) has to be useful right away: Alice has unread messages from three people, so the
/// unread badges and the AI summary can be tried without any preparation.</summary>
[Collection(ApiCollection.Name)]
public sealed class DemoSeedTests(ApiFactory factory)
{
    private const string Password = "Migly-demo-2026";

    private async Task SeedAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync(Password, CancellationToken.None);
    }

    private async Task<Dictionary<string, int>> UnreadByChatAsync(string person)
    {
        var user = await TestUsers.LoginAsync(factory, $"{person}@migly.local", person, Password);
        var chats = await user.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations");
        return chats.ToDictionary(c => c.Peer?.Username ?? "group", c => c.UnreadCount);
    }

    [Fact]
    public async Task Alice_has_unread_messages_from_bohdan_dmytro_and_iryna_and_seeding_twice_changes_nothing()
    {
        await SeedAsync();
        await SeedAsync(); // idempotent

        var alice = await UnreadByChatAsync("alice");

        alice["bohdan"].ShouldBe(5);
        alice["dmytro"].ShouldBe(2);
        alice["iryna"].ShouldBe(1);
        alice["kate"].ShouldBe(0);
        alice["group"].ShouldBe(0);
    }

    [Fact]
    public async Task The_previous_unread_state_of_the_crew_chat_is_kept_and_nobody_else_has_unread_direct_messages()
    {
        await SeedAsync();

        // Iryna and Dmytro have not read the last 4 messages of the crew chat; one of those 4 is their own and never counts as unread.
        (await UnreadByChatAsync("iryna"))["group"].ShouldBe(3);
        (await UnreadByChatAsync("dmytro"))["group"].ShouldBe(3);
        (await UnreadByChatAsync("bohdan")).Values.ShouldAllBe(unread => unread == 0);
        (await UnreadByChatAsync("kate")).Values.ShouldAllBe(unread => unread == 0);
    }

    [Fact]
    public async Task The_unread_messages_are_plain_text_without_a_stray_attachment()
    {
        await SeedAsync();
        var alice = await TestUsers.LoginAsync(factory, "alice@migly.local", "alice", Password);
        var chats = await alice.GetJsonAsync<List<ConversationDto>>("/api/v1/conversations");
        var withBohdan = chats.Single(c => c.Peer?.Username == "bohdan");

        var page = await alice.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{withBohdan.Id}/messages?limit=50");

        var unread = page.Items.TakeLast(5).ToList();
        unread.ShouldAllBe(m => m.Sender.Username == "bohdan");
        unread.ShouldAllBe(m => m.Attachments.Count == 0); // the seeder attaches a file to any text containing "PDF"
    }
}
