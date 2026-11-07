using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AccountAdminAndOpsTests(ApiFactory factory)
{
    [Fact]
    public async Task Deleting_account_frees_username_transfers_ownership_and_keeps_messages()
    {
        var owner = await TestUsers.CreateAsync(factory, "del");
        var bob = await TestUsers.CreateAsync(factory, "del");
        var carol = await TestUsers.CreateAsync(factory, "del");
        var group = await owner.CreateGroupAsync("Goodbye", bob, carol);
        var message = await owner.SendAsync(group.Id, "I am leaving soon");

        var wrong = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/users/me") { Content = JsonContent.Create(new DeleteAccountRequest("nope")) };
        (await owner.Client.SendAsync(wrong)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/users/me") { Content = JsonContent.Create(new DeleteAccountRequest(owner.Password)) };
        (await owner.Client.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await TestUsers.RefreshAsync(factory, owner.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var members = await bob.GetJsonAsync<List<MemberDto>>($"/api/v1/conversations/{group.Id}/members");
        members.ShouldContain(m => m.Role == "Owner");
        members.ShouldNotContain(m => m.UserId == owner.Id);

        var page = await bob.GetJsonAsync<MessagePageDto>($"/api/v1/conversations/{group.Id}/messages");
        var kept = page.Items.Single(m => m.Id == message.Id);
        kept.Sender.IsDeleted.ShouldBeTrue();
        kept.Body.ShouldBe("I am leaving soon");

        var client = factory.CreateClient();
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest($"new-{owner.Email}", TestUsers.Password, owner.Username, "New owner of the name"));
        reuse.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        factory.Emails.LatestToken($"new-{owner.Email}", "/verify-email").ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Username_change_conflicts_case_insensitively()
    {
        var alice = await TestUsers.CreateAsync(factory, "uname");
        var bob = await TestUsers.CreateAsync(factory, "uname");

        var taken = await bob.Client.PutAsJsonAsync("/api/v1/users/me/username", new ChangeUsernameRequest(alice.Username.ToUpperInvariant()));
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var fresh = TestUsers.Unique("renamed");
        var ok = await bob.Client.PutAsJsonAsync("/api/v1/users/me/username", new ChangeUsernameRequest(fresh));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<MeDto>(TestUsers.Json))!.Username.ShouldBe(fresh);
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_role_and_never_expose_messages()
    {
        var user = await TestUsers.CreateAsync(factory, "plain");
        (await user.Client.GetAsync("/api/v1/admin/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var adminSeed = await TestUsers.CreateAsync(factory, "admin");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MessengerDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE users SET role = 'Admin' WHERE id = {adminSeed.Id}");
        }

        var admin = await TestUsers.LoginAsync(factory, adminSeed.Email, adminSeed.Username);
        var page = await admin.GetJsonAsync<PagedResult<AdminUserDto>>("/api/v1/admin/users?limit=2&offset=0");
        page.Items.Count.ShouldBe(2);
        page.Total.ShouldBeGreaterThanOrEqualTo(2);
        page.Next.ShouldBe(page.Total > 2 ? 2 : null);

        (await admin.Client.PostAsync($"/api/v1/admin/users/{user.Id}/suspend", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, user.Password));
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await TestUsers.RefreshAsync(factory, user.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await admin.Client.PostAsync($"/api/v1/admin/users/{user.Id}/unsuspend", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var audit = await admin.GetJsonAsync<PagedResult<AuditLogDto>>("/api/v1/admin/audit?limit=50");
        audit.Items.ShouldContain(a => a.Action == "admin.user_suspended" && a.EntityId == user.Id.ToString());
    }

    [Fact]
    public async Task Health_docs_and_openapi_are_public()
    {
        var client = factory.CreateClient();

        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/docs")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var openApi = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var paths = openApi.GetProperty("paths");
        paths.TryGetProperty("/api/v1/conversations/{id}/messages", out _).ShouldBeTrue();
        paths.TryGetProperty("/api/v1/auth/login", out _).ShouldBeTrue();
        openApi.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_api_route_is_json_404_and_security_headers_are_set()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }
}

public sealed class RateLimitTests : IAsyncLifetime
{
    private readonly StrictFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeAsync();

    public Task DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Auth_endpoints_return_429_with_retry_after()
    {
        var client = _factory.CreateClient();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever-pass"));
        }

        last!.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        last.Headers.RetryAfter.ShouldNotBeNull();
        last.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    private sealed class StrictFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["RateLimiting:AuthPerMinute"] = "3" };
    }
}
