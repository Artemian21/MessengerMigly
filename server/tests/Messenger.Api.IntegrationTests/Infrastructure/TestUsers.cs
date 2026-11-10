using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api.Contracts;
using Messenger.Application.Common;
using Shouldly;

namespace Messenger.Api.IntegrationTests.Infrastructure;

public sealed record TestUser(Guid Id, string Email, string Username, string Password, HttpClient Client, string AccessToken, string RefreshCookie);

public static class TestUsers
{
    public const string Password = "S3cure-pass-123";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 10, 32)];

    public static async Task<TestUser> CreateAsync(ApiFactory factory, string prefix = "user")
    {
        var username = Unique(prefix);
        var email = $"{username}@example.com";
        var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest(email, Password, username, $"{prefix} tester"));
        register.StatusCode.ShouldBe(HttpStatusCode.Accepted, await register.Content.ReadAsStringAsync());

        var token = factory.Emails.LatestToken(email, "/verify-email");
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(token))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        return await LoginAsync(factory, email, username);
    }

    public static async Task<TestUser> LoginAsync(ApiFactory factory, string email, string username, string password = Password)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        login.StatusCode.ShouldBe(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var body = (await login.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.AccessToken);
        return new TestUser(body.User.Id, email, username, password, client, body.AccessToken, ReadRefreshCookie(login)!);
    }

    public static string? ReadRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var cookie = cookies.FirstOrDefault(c => c.StartsWith("rt=", StringComparison.Ordinal) && !c.StartsWith("rt=;", StringComparison.Ordinal));
        return cookie?[3..cookie.IndexOf(';')];
    }

    public static Task<HttpResponseMessage> RefreshAsync(ApiFactory factory, string refreshCookie, bool withCsrfHeader = true)
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", $"rt={refreshCookie}");
        if (withCsrfHeader)
        {
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        }

        return client.SendAsync(request);
    }

    public static async Task<MessageDto> SendDirectAsync(this TestUser from, Guid peerId, string body, Guid? clientMessageId = null)
    {
        var response = await from.Client.PostAsJsonAsync($"/api/v1/conversations/direct/{peerId}/messages",
            new SendMessageRequest(clientMessageId ?? Guid.NewGuid(), body, null, null));
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MessageDto>(Json))!;
    }

    public static async Task<MessageDto> SendAsync(this TestUser from, Guid conversationId, string body, Guid? replyTo = null)
    {
        var response = await from.Client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/messages",
            new SendMessageRequest(Guid.NewGuid(), body, replyTo, null));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<MessageDto>(Json))!;
    }

    public static async Task<ConversationDto> CreateGroupAsync(this TestUser owner, string title, params TestUser[] members)
    {
        var response = await owner.Client.PostAsJsonAsync("/api/v1/conversations/groups",
            new CreateGroupRequest(title, members.Select(m => m.Id).ToList()));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ConversationDto>(Json))!;
    }

    public static async Task<T> GetJsonAsync<T>(this TestUser user, string url)
    {
        var response = await user.Client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
}
