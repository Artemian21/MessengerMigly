using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Messenger.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace Messenger.Api.Infrastructure;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string SendMessage = "send-message";
    public const string Summary = "ai-summary";
}

public sealed class RateLimitingOptions
{
    public int GlobalPerMinute { get; set; } = 60;
    public int AuthPerMinute { get; set; } = 10;
    public int SendBurst { get; set; } = 10;
    public int SendPerMinute { get; set; } = 30;
    public int SummaryPerMinute { get; set; } = 5;
}

public static class RateLimitingSetup
{
    /// <summary>API-05: global per-IP window, stricter auth window, per-user token bucket for sending; 429 + Retry-After.</summary>
    public static IServiceCollection AddMessengerRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                http.Request.Path.StartsWithSegments("/health")
                    ? RateLimitPartition.GetNoLimiter("health")
                    : RateLimitPartition.GetFixedWindowLimiter(ClientIp(http), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.GlobalPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }));
            limiter.AddPolicy(RateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.AuthPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(RateLimitPolicies.SendMessage, http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirstValue(ClaimTypesShort.Sub) ?? ClientIp(http),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = options.SendBurst,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(60.0 / Math.Max(1, options.SendPerMinute)),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            // Each summary costs money: a small per-user window, separate from the message-sending bucket.
            limiter.AddPolicy(RateLimitPolicies.Summary, http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue(ClaimTypesShort.Sub) ?? ClientIp(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.SummaryPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(60);
                var http = context.HttpContext;
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests",
                        Type = "https://httpstatuses.io/429",
                        Detail = "Rate limit exceeded. Retry after the time given in the Retry-After header.",
                    },
                });
            };
        });
        return services;
    }

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>NFR-03: security headers. The CSP is relaxed only for the API reference UI under /docs.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
                               "font-src 'self' data:; connect-src 'self' ws: wss:; worker-src 'self'; manifest-src 'self'; " +
                               "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task Invoke(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        var path = http.Request.Path;
        if (!path.StartsWithSegments("/docs") && !path.StartsWithSegments("/scalar") && !path.StartsWithSegments("/openapi"))
        {
            headers.ContentSecurityPolicy = Csp;
        }

        return next(http);
    }
}

/// <summary>NFR-02: CSRF protection for cookie-authenticated endpoints (refresh, logout): a custom header that browsers
/// never add cross-site, plus Origin / Sec-Fetch-Site checks.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireSameOriginAttribute : Attribute, IActionFilter
{
    public const string HeaderName = "X-Requested-With";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        var origin = request.Headers.Origin.ToString();
        var sameOrigin = string.IsNullOrEmpty(origin) || IsSameOrigin(origin, request);
        var siteOk = string.IsNullOrEmpty(fetchSite) || fetchSite is "same-origin" or "none";
        if (string.IsNullOrEmpty(request.Headers[HeaderName]) || !sameOrigin || !siteOk)
        {
            context.Result = Problems.ToActionResult(
                Domain.Common.Error.Forbidden("Cross-site request rejected."), context.HttpContext);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    private static bool IsSameOrigin(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var allowed = request.HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        return string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
               || allowed.Contains(origin.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>AUTH-05 / D-08: the refresh token lives only in an HttpOnly, SameSite=Strict cookie scoped to /api/v1/auth.</summary>
public static class RefreshCookie
{
    public const string Name = "rt";
    public const string Path = "/api/v1/auth";

    public static void Set(HttpResponse response, string token, DateTimeOffset expires) =>
        response.Cookies.Append(Name, token, Options(response.HttpContext, expires));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, Options(response.HttpContext, null));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    private static CookieOptions Options(HttpContext http, DateTimeOffset? expires)
    {
        var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
        return new CookieOptions
        {
            HttpOnly = true,
            // Browsers accept Secure cookies on http://localhost; plain-http hosts other than localhost only in Development.
            Secure = http.Request.IsHttps || !env.IsDevelopment(),
            SameSite = SameSiteMode.Strict,
            Path = Path,
            Expires = expires,
            IsEssential = true,
        };
    }
}
