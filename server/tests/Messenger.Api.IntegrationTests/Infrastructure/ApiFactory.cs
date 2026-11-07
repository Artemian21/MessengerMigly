using System.Collections.Concurrent;
using Messenger.Application.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Messenger.Api.IntegrationTests.Infrastructure;

/// <summary>Captures outgoing emails so tests can follow verification / reset links.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public string LatestToken(string to, string pathFragment)
    {
        var message = Sent.Reverse().First(m => m.To.Equals(to, StringComparison.OrdinalIgnoreCase) && m.TextBody.Contains(pathFragment));
        var start = message.TextBody.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = message.TextBody.IndexOfAny(['\n', '\r', ' '], start);
        return Uri.UnescapeDataString(message.TextBody[start..(end < 0 ? message.TextBody.Length : end)]);
    }
}

/// <summary>Real PostgreSQL in a container (OPS-03). One container per factory; tests isolate data with unique users.</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("messenger")
        .WithUsername("messenger")
        .WithPassword("messenger")
        .Build();

    public CapturingEmailSender Emails { get; } = new();

    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("PasswordHashing:WorkFactor", "4");
        builder.UseSetting("RateLimiting:GlobalPerMinute", "100000");
        builder.UseSetting("RateLimiting:AuthPerMinute", "100000");
        builder.UseSetting("RateLimiting:SendBurst", "100000");
        builder.UseSetting("RateLimiting:SendPerMinute", "100000");
        builder.UseSetting("Presence:GraceSeconds", "1");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
