using System.Net;
using System.Net.Http.Json;
using System.Text;
using Messenger.Api.IntegrationTests.Infrastructure;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

/// <summary>API-03: garbage input is a 4xx with a problem document, never a 5xx. NUL (\u0000) used to reach PostgreSQL,
/// which cannot store it, and surfaced as a misleading 503.</summary>
[Collection(ApiCollection.Name)]
public sealed class InputHardeningTests(ApiFactory factory)
{
    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Theory]
    [InlineData("/api/v1/auth/login", """{"email":"a\u0000b@example.com","password":"whatever-123"}""")]
    [InlineData("/api/v1/auth/register", """{"email":"a\u0000b@example.com","password":"S3cure-pass-123","username":"nul.user","displayName":"N"}""")]
    [InlineData("/api/v1/auth/forgot-password", """{"email":"a\u0000b@example.com"}""")]
    [InlineData("/api/v1/auth/resend-verification", """{"email":"a\u0000b@example.com"}""")]
    public async Task Anonymous_endpoints_reject_nul_in_email_with_422(string url, string body)
    {
        var response = await factory.CreateClient().PostAsync(url, Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("/api/v1/search/messages?q=%00%00")]
    [InlineData("/api/v1/users?q=%00%00")]
    public async Task Search_rejects_nul_with_422(string url)
    {
        var user = await TestUsers.CreateAsync(factory, "nulsearch");

        var response = await user.Client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Push_unsubscribe_rejects_nul_with_422()
    {
        var user = await TestUsers.CreateAsync(factory, "nulpush");
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/push/subscriptions") { Content = Json("""{"endpoint":"a\u0000b"}""") };

        var response = await user.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Search_nul_message_is_translated_to_ukrainian()
    {
        var user = await TestUsers.CreateAsync(factory, "nulukr");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/search/messages?q=%00%00");
        request.Headers.AcceptLanguage.ParseAdd("uk");

        var body = await (await user.Client.SendAsync(request)).Content.ReadAsStringAsync();

        body.ShouldContain("Текст пошуку містить заборонені керівні символи.");
    }
}
