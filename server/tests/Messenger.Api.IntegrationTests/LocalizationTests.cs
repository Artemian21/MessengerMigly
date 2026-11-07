using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Messenger.Api.Contracts;
using Messenger.Api.IntegrationTests.Infrastructure;
using Shouldly;

namespace Messenger.Api.IntegrationTests;

/// <summary>API messages and emails follow Accept-Language (en default, uk, ru).</summary>
[Collection(ApiCollection.Name)]
public sealed class LocalizationTests(ApiFactory factory)
{
    private HttpClient Client(string language)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return client;
    }

    [Fact]
    public async Task Validation_errors_are_translated_to_ukrainian()
    {
        var response = await Client("uk").PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest("bad", "short", "x", "Name"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("Помилка перевірки даних");
        problem.GetProperty("errors").GetProperty("email")[0].GetString().ShouldBe("Неправильний формат email.");
        problem.GetProperty("errors").GetProperty("password")[0].GetString().ShouldBe("Пароль має містити щонайменше 10 символів.");
    }

    [Fact]
    public async Task Auth_errors_are_ukrainian_or_english_and_unsupported_languages_fall_back_to_english()
    {
        var uk = await Client("uk-UA,uk;q=0.9").PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever-123"));
        var en = await Client("en").PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever-123"));
        var other = await Client("ru-RU").PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nobody@example.com", "whatever-123"));

        (await uk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("Неправильний email або пароль.");
        (await en.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("Invalid email or password.");
        (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("Invalid email or password.");
    }

    [Fact]
    public async Task Not_found_messages_translate_the_entity_name()
    {
        var user = await TestUsers.CreateAsync(factory, "l10n");
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/conversations/{Guid.NewGuid()}");
        request.Headers.AcceptLanguage.ParseAdd("uk");

        var response = await user.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().ShouldBe("Розмову: не знайдено.");
    }

    [Fact]
    public async Task Verification_email_is_sent_in_the_request_language()
    {
        var username = TestUsers.Unique("lang");
        var email = $"{username}@example.com";

        await Client("uk").PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, TestUsers.Password, username, "Олена"));

        var message = factory.Emails.Sent.Last(m => m.To == email);
        message.Subject.ShouldBe("Підтвердьте акаунт Migly");
        message.TextBody.ShouldStartWith("Вітаємо, Олена!");
    }
}
