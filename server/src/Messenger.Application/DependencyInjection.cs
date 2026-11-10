using FluentValidation;
using Messenger.Application.Auth;
using Messenger.Application.Common;
using Messenger.Application.Conversations;
using Messenger.Application.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Messenger.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // Order matters (outermost first): Logging → Validation → Authorization → Transaction → Handler.
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            cfg.AddOpenBehavior(typeof(TransactionBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly, includeInternalTypes: true);

        services.AddScoped<MessageReader>();
        services.AddScoped<MessageSender>();
        services.AddScoped<ConversationReader>();
        services.AddScoped<SessionIssuer>();
        services.AddScoped<UserTokenIssuer>();
        services.AddScoped<Summaries.SummaryJobProcessor>();
        return services;
    }
}
