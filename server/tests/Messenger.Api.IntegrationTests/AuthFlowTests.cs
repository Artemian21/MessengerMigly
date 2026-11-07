using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Messenger.Application.Common;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AuthFlowTests(ApiFactory factory)
{
    [Fact]
    public async Task Register_verify_login_refresh_rotation_reuse_detection_and_logout()
    {
        var user = await TestUsers.CreateAsync(factory, "flow");

        (await user.Client.GetAsync("/api/v1/users/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var first = await TestUsers.RefreshAsync(factory, user.RefreshCookie);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = TestUsers.ReadRefreshCookie(first);
        rotated.ShouldNotBeNull();
        rotated.ShouldNotBe(user.RefreshCookie);
        first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("rt=")).ShouldContain("httponly", Case.Insensitive);
        first.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("rt=")).ShouldContain("samesite=strict", Case.Insensitive);

        // Second rotation, then the first (twice-rotated) cookie is replayed after the grace window would have expired
        // for it: simulate theft by replaying the very first cookie after rotating again.
        var second = await TestUsers.RefreshAsync(factory, rotated!);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var latest = TestUsers.ReadRefreshCookie(second)!;

        await Task.Delay(TimeSpan.FromSeconds(1));
        var stale = await TestUsers.RefreshAsync(factory, user.RefreshCookie);
        // Within grace the first cookie only yields an access token; the family stays alive.
        stale.StatusCode.ShouldBe(HttpStatusCode.OK);
        TestUsers.ReadRefreshCookie(stale).ShouldBeNull();

        var logoutClient = factory.CreateClient();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", $"rt={latest}");
        logout.Headers.Add("X-Requested-With", "XMLHttpRequest");
        (await logoutClient.SendAsync(logout)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await TestUsers.RefreshAsync(factory, latest)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reuse_outside_grace_revokes_the_whole_session()
    {
        await using var shortGrace = new ShortGraceFactory();
        await shortGrace.InitializeAsync();
        var user = await TestUsers.CreateAsync(shortGrace, "reuse");

        var rotated = TestUsers.ReadRefreshCookie(await TestUsers.RefreshAsync(shortGrace, user.RefreshCookie))!;
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        (await TestUsers.RefreshAsync(shortGrace, user.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TestUsers.RefreshAsync(shortGrace, rotated)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_without_csrf_header_is_rejected()
    {
        var user = await TestUsers.CreateAsync(factory, "csrf");

        (await TestUsers.RefreshAsync(factory, user.RefreshCookie, withCsrfHeader: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Requests_without_token_are_401()
    {
        var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/users/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/conversations")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tampered_token_is_401()
    {
        var user = await TestUsers.CreateAsync(factory, "tamper");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken[..^3] + "abc");

        (await client.GetAsync("/api/v1/users/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_password_is_401_with_generic_message()
    {
        var user = await TestUsers.CreateAsync(factory, "wrongpw");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, "not-the-password"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().ShouldBe("Invalid email or password.");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unverified_account_cannot_sign_in_and_duplicate_registration_is_neutral()
    {
        var client = factory.CreateClient();
        var username = TestUsers.Unique("unverified");
        var request = new RegisterRequest($"{username}@example.com", TestUsers.Password, username, "Unverified");

        (await client.PostAsJsonAsync("/api/v1/auth/register", request)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await client.PostAsJsonAsync("/api/v1/auth/register", request with { Username = TestUsers.Unique("other") }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(request.Email, request.Password));
        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Verification_token_is_single_use()
    {
        var client = factory.CreateClient();
        var username = TestUsers.Unique("once");
        var email = $"{username}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, TestUsers.Password, username, "Once"));
        var token = factory.Emails.LatestToken(email, "/verify-email");

        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(token))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest(token))).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new TokenRequest("garbage"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Lockout_after_repeated_failures()
    {
        var user = await TestUsers.CreateAsync(factory, "lock");
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, "wrong-password"));
        }

        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(user.Email, user.Password));

        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Password_reset_revokes_every_session()
    {
        var user = await TestUsers.CreateAsync(factory, "reset");
        var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(user.Email))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = factory.Emails.LatestToken(user.Email, "/reset-password");
        (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "Brand-new-pass-1")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await TestUsers.RefreshAsync(factory, user.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await TestUsers.LoginAsync(factory, user.Email, user.Username, "Brand-new-pass-1");
    }

    [Fact]
    public async Task Change_password_keeps_current_session_and_revokes_others()
    {
        var user = await TestUsers.CreateAsync(factory, "change");
        var other = await TestUsers.LoginAsync(factory, user.Email, user.Username);

        var response = await user.Client.PostAsJsonAsync("/api/v1/auth/change-password",
            new ChangePasswordRequest(user.Password, "Changed-pass-123"));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await TestUsers.RefreshAsync(factory, user.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TestUsers.RefreshAsync(factory, other.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var wrong = await user.Client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest("nope", "Another-pass-123"));
        wrong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked()
    {
        var user = await TestUsers.CreateAsync(factory, "sessions");
        var second = await TestUsers.LoginAsync(factory, user.Email, user.Username);

        var sessions = await user.GetJsonAsync<List<SessionDto>>("/api/v1/auth/sessions");
        sessions.Count.ShouldBe(2);
        sessions.Count(s => s.Current).ShouldBe(1);

        var target = sessions.Single(s => !s.Current);
        (await user.Client.DeleteAsync($"/api/v1/auth/sessions/{target.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TestUsers.RefreshAsync(factory, second.RefreshCookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Validation_errors_are_422_problem_details_with_field_errors()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("not-an-email", "short", "A", ""));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        errors.TryGetProperty("email", out _).ShouldBeTrue();
        errors.TryGetProperty("password", out _).ShouldBeTrue();
        errors.TryGetProperty("username", out _).ShouldBeTrue();
        errors.TryGetProperty("displayName", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Malformed_json_is_422_not_500()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/login",
            new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private sealed class ShortGraceFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Auth:RefreshGraceSeconds"] = "1" };
    }
}
