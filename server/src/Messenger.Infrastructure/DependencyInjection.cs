using Messenger.Application.Abstractions;
using Messenger.Infrastructure.Notifications;
using Messenger.Infrastructure.Persistence;
using Messenger.Infrastructure.Security;
using Messenger.Infrastructure.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Messenger.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        services.AddScoped<DomainEventBuffer>();
        services.AddScoped<IDomainEventCollector>(sp => sp.GetRequiredService<DomainEventBuffer>());
        services.AddScoped<DomainEventInterceptor>();
        services.AddScoped<DomainEventDispatcher>();
        services.AddDbContext<MessengerDbContext>((sp, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>()));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<MessengerDbContext>());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAuthTokenRepository, AuthTokenRepository>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<DatabaseMigrator>();
        services.AddScoped<DemoSeeder>();

        services.AddSingleton<IClock, SystemClock>();
        services.Configure<PasswordHashingOptions>(configuration.GetSection("PasswordHashing"));
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.AddSingleton<ITokenService, JwtTokenService>();

        services.Configure<EmailOptions>(configuration.GetSection("Email"));
        var provider = configuration["Email:Provider"] ?? "Mailpit";
        if (provider.Equals("Brevo", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmailSender, BrevoEmailSender>();
        }
        else if (provider.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, NullEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }

        // AI summary: in-memory jobs + queue, a background worker, and the HTTP client to the Python service (ai/).
        // AiOptions itself is registered by the API (like AuthOptions).
        services.AddSingleton<ISummaryJobStore, InMemorySummaryJobStore>();
        services.AddSingleton<SummaryJobQueue>();
        services.AddSingleton<ISummaryJobQueue>(sp => sp.GetRequiredService<SummaryJobQueue>());
        services.AddHttpClient<IConversationSummarizer, HttpConversationSummarizer>(
            (sp, client) => HttpConversationSummarizer.Configure(client, sp.GetRequiredService<AiOptions>()));
        services.AddHostedService<SummaryWorker>();

        services.Configure<VapidOptions>(configuration.GetSection("Vapid"));
        services.AddHttpClient<IPushSender, WebPushSender>();
        return services;
    }
}
