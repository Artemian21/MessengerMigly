using System.Collections.Concurrent;
using Messenger.Application.Abstractions;
using Messenger.Application.Summaries;
using Messenger.Domain.Common;
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

/// <summary>Stands in for the Python AI service. Tests set <see cref="Next"/> / <see cref="Gate"/> and read <see cref="Calls"/>;
/// the "api" collection runs its classes one after another, so tests call <see cref="Reset"/> first.</summary>
public sealed class FakeSummarizer : IConversationSummarizer
{
    public ConcurrentQueue<(string Language, IReadOnlyList<SummaryMessage> Messages)> Calls { get; } = new();

    /// <summary>The next answer; null means the default "- fake summary".</summary>
    public Result<string>? Next { get; set; }

    /// <summary>When set, the call waits for it before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public void Reset()
    {
        Calls.Clear();
        Next = null;
        Gate = null;
    }

    public async Task<Result<string>> SummarizeAsync(string language, IReadOnlyList<SummaryMessage> messages, CancellationToken ct)
    {
        Calls.Enqueue((language, messages));
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return Next ?? "- fake summary";
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

    public FakeSummarizer Summarizer { get; } = new();

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
        builder.UseSetting("RateLimiting:SummaryPerMinute", "100000");
        builder.UseSetting("Presence:GraceSeconds", "1");
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:SharedSecret", "test-shared-secret-0123456789");
        builder.UseSetting("Ai:MaxMessages", "4"); // small so the truncation test needs only a few messages
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            services.RemoveAll<IConversationSummarizer>();
            services.AddSingleton<IConversationSummarizer>(Summarizer);
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
