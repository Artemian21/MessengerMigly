using Microsoft.AspNetCore.Authentication;
using System.Text;
using Messenger.Api.Controllers;
using Messenger.Api.Infrastructure;
using Messenger.Api.Realtime;
using Messenger.Application;
using Messenger.Application.Abstractions;
using Messenger.Infrastructure;
using Messenger.Infrastructure.Persistence;
using Messenger.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Messenger.Api;

public static class LoggingSetup
{
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration().WriteTo.Console(new RenderedCompactJsonFormatter()).CreateLogger();

    /// <summary>API-11: compact JSON logs. Request logs carry method, path (never the query string), status, userId,
    /// duration and traceId. Bodies, headers and cookies are never logged.</summary>
    public static void AddMessengerLogging(this WebApplicationBuilder builder) =>
        builder.Services.AddSerilog((services, config) => config
            .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(new RenderedCompactJsonFormatter()));
}

public static class ApiSetup
{
    public static void AddMessengerServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        builder.AddMessengerLogging();
        ValidateSecrets(builder);

        services.AddApplication();
        services.AddInfrastructure(configuration);
        services.AddSingleton(configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions());
        services.AddSingleton(configuration.GetSection("App").Get<AppOptions>() ?? new AppOptions());

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions.TryAdd("traceId", System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
            context.ProblemDetails.Title = ApiLocalizer.Translate(context.ProblemDetails.Title ?? Problems.TitleFor(context.ProblemDetails.Status ?? 500));
            context.ProblemDetails.Detail = context.ProblemDetails.Detail is null ? null : ApiLocalizer.Translate(context.ProblemDetails.Detail);
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddControllers().ConfigureApiBehaviorOptions(options =>
        {
            // P-19: malformed or invalid input is 422 with field errors, never 400/500.
            options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value is { Errors.Count: > 0 })
                    .GroupBy(e => e.Key.TrimStart('$', '.') is { Length: > 0 } key ? key : "body")
                    .ToDictionary(
                        g => g.Key,
                        g => g.SelectMany(e => e.Value!.Errors).Select(x => ApiLocalizer.TranslateModelError(x.ErrorMessage)).Distinct().ToArray());
                var problem = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Title = ApiLocalizer.Translate("Validation failed"),
                    Type = "https://httpstatuses.io/422",
                    Detail = ApiLocalizer.Translate("One or more validation errors occurred."),
                };
                problem.Extensions["code"] = "validation";
                problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                return new UnprocessableEntityObjectResult(problem) { ContentTypes = { "application/problem+json" } };
            };
        });

        services.AddMessengerOpenApi();
        services.AddMessengerRateLimiting(configuration);
        AddAuth(services, configuration);

        services.AddSignalR(o =>
        {
            o.MaximumReceiveMessageSize = 32 * 1024;
            o.EnableDetailedErrors = false;
        });
        services.AddSingleton<IUserIdProvider, SubUserIdProvider>();
        services.AddSingleton<PresenceTracker>();
        services.AddSingleton<IPresenceTracker>(sp => sp.GetRequiredService<PresenceTracker>());
        services.AddSingleton<IRealtimeNotifier, SignalRRealtimeNotifier>();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (configuration.GetValue("ForwardedHeaders:TrustProxy", false))
            {
                // P-20: Render's proxy addresses are not static; trust exactly one hop.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
                options.ForwardLimit = 1;
            }
        });

        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(180));
    }

    private static void AddAuth(IServiceCollection services, IConfiguration configuration)
    {
        var secret = configuration["Jwt:Secret"] ?? string.Empty;
        var auth = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypesShort.Sub,
                    RoleClaimType = ClaimTypesShort.Role,
                };
                options.Events = new JwtBearerEvents
                {
                    // WebSockets cannot send headers: SignalR passes the token in the query (never logged, P-10).
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments(ChatHub.Path))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };
            })
            .AddCookie(ExternalAuthController.ExternalScheme, options =>
            {
                options.Cookie.Name = "ext";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        // AUTH-09: providers are registered only when their credentials are configured.
        if (configuration["Authentication:Google:ClientId"] is { Length: > 0 } googleId)
        {
            auth.AddGoogle("Google", o =>
            {
                o.SignInScheme = ExternalAuthController.ExternalScheme;
                o.ClientId = googleId;
                o.ClientSecret = configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
                o.ClaimActions.MapJsonKey("email_verified", "email_verified");
            });
        }

        if (configuration["Authentication:GitHub:ClientId"] is { Length: > 0 } githubId)
        {
            auth.AddGitHub("GitHub", o =>
            {
                o.SignInScheme = ExternalAuthController.ExternalScheme;
                o.ClientId = githubId;
                o.ClientSecret = configuration["Authentication:GitHub:ClientSecret"] ?? string.Empty;
                o.Scope.Add("user:email");
            });
        }

        services.AddAuthorization();
    }

    /// <summary>Production refuses to start with a missing, short or development JWT secret.</summary>
    private static void ValidateSecrets(WebApplicationBuilder builder)
    {
        var secret = builder.Configuration["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret) && (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")))
        {
            builder.Configuration["Jwt:Secret"] = JwtOptions.DevelopmentSecret;
            return;
        }

        if (string.IsNullOrEmpty(secret) || Encoding.UTF8.GetByteCount(secret) < 32 ||
            (secret == JwtOptions.DevelopmentSecret && !builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")))
        {
            throw new InvalidOperationException("Jwt:Secret must be set to a random value of at least 32 bytes (see .env.example).");
        }
    }

    public static void UseMessengerPipeline(this WebApplication app)
    {
        // First, so every later component (including the exception handler) sees the request language.
        app.UseRequestLocalization(ApiLocalizer.Options());
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            options.GetLevel = (http, _, ex) =>
                ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
            options.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                diagnostics.Set("UserId", http.User.FindFirst(ClaimTypesShort.Sub)?.Value ?? "anonymous");
                diagnostics.Set("TraceId", System.Diagnostics.Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier);
            };
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous().DisableRateLimiting();
        app.MapScalarApiReference("/docs", options => options
                .WithTitle("Migly Messenger API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .AddPreferredSecuritySchemes("Bearer"))
            .AllowAnonymous().DisableRateLimiting();
        app.MapControllers();
        app.MapHub<ChatHub>(ChatHub.Path, o => o.CloseOnAuthenticationExpiration = true);

        // Unknown API routes are JSON 404s; everything else falls back to the SPA.
        app.Map("/api/{**rest}", (HttpContext http) => Results.Problem(statusCode: 404, title: "Not found",
            detail: "No API endpoint matches this path.", type: "https://httpstatuses.io/404"));
        app.MapFallbackToFile("index.html").DisableRateLimiting();
    }
}

/// <summary>Maintenance commands: `dotnet run -- migrate | seed | promote &lt;email&gt;`.</summary>
public static class Cli
{
    private static readonly string[] Commands = ["migrate", "seed", "promote"];

    public static bool IsCommand(string arg) => Commands.Contains(arg);

    public static bool TryGetCommand(string[] args, out string command)
    {
        command = args.FirstOrDefault(IsCommand) ?? string.Empty;
        return command.Length > 0;
    }

    public static async Task<int> RunAsync(WebApplication app, string command, string[] args)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
        switch (command)
        {
            case "seed":
                var password = app.Configuration["Seed:Password"];
                if (string.IsNullOrEmpty(password))
                {
                    Log.Error("Set Seed:Password (env Seed__Password) to the password for demo accounts");
                    return 2;
                }

                await services.GetRequiredService<DemoSeeder>().SeedAsync(password, CancellationToken.None);
                break;
            case "promote":
                var email = args.SkipWhile(a => a != "promote").Skip(1).FirstOrDefault();
                var users = services.GetRequiredService<IUserRepository>();
                var user = email is null ? null : await users.GetByEmailAsync(Domain.Common.InputRules.NormalizeEmail(email), CancellationToken.None);
                if (user is null)
                {
                    Log.Error("Usage: promote <email> (user must exist)");
                    return 2;
                }

                user.PromoteToAdmin();
                await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(CancellationToken.None);
                Log.Information("User promoted to Admin");
                break;
        }

        return 0;
    }
}
