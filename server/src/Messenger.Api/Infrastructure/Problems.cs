using System.Diagnostics;
using Messenger.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Messenger.Api.Infrastructure;

/// <summary>API-03: maps typed errors to RFC 9457 ProblemDetails. Validation → 422 with field errors.</summary>
public static class Problems
{
    public static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Gone => StatusCodes.Status410Gone,
        ErrorType.Locked => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status400BadRequest,
    };

    public static ObjectResult ToActionResult(Error error, HttpContext http)
    {
        var status = StatusFor(error.Type);
        ProblemDetails problem = error.Fields is { Count: > 0 }
            ? new ValidationProblemDetails(error.Fields.ToDictionary(f => f.Key, f => f.Value.Select(ApiLocalizer.Translate).ToArray()))
            : new ProblemDetails();
        problem.Status = status;
        problem.Title = ApiLocalizer.Translate(error.Type == ErrorType.Validation ? "Validation failed" : TitleFor(status));
        problem.Detail = ApiLocalizer.Translate(error.Message);
        problem.Type = $"https://httpstatuses.io/{status}";
        problem.Extensions["code"] = error.Code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
        if (error.Type == ErrorType.Locked)
        {
            http.Response.Headers.RetryAfter = "900";
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    public static string TitleFor(int status) => status switch
    {
        400 => "Bad request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        409 => "Conflict",
        410 => "Gone",
        422 => "Validation failed",
        429 => "Too many requests",
        503 => "Service unavailable",
        _ => "Server error",
    };
}

/// <summary>Last line of defence: never leaks internals. Database outages become 503 (Standard: storage failure handling).</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static PostgresException? FindPostgres(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var postgres = FindPostgres(exception);
        var (status, title) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, "Bad request"),
            Application.Abstractions.UniqueConstraintException => (StatusCodes.Status409Conflict, "Conflict"),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested => (499, "Client closed request"),
            // SQLSTATE class 22 = data exception (e.g. a NUL byte in text): the request was bad, the database is fine.
            _ when postgres?.SqlState?.StartsWith("22", StringComparison.Ordinal) == true => (StatusCodes.Status422UnprocessableEntity, "Validation failed"),
            NpgsqlException or TimeoutException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable"),
            Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException => (StatusCodes.Status503ServiceUnavailable, "Service unavailable"),
            InvalidOperationException { InnerException: NpgsqlException } => (StatusCodes.Status503ServiceUnavailable, "Service unavailable"),
            _ => (StatusCodes.Status500InternalServerError, "Server error"),
        };

        if (status >= 500 && status != 503)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
        }
        else if (status == 422)
        {
            logger.LogWarning("Rejected a value the database cannot store (SQLSTATE {SqlState}) on {Method} {Path}", postgres?.SqlState,
                http.Request.Method, http.Request.Path);
        }
        else if (status == 503)
        {
            logger.LogWarning("Database unavailable: {Error}", exception.GetType().Name);
        }

        http.Response.StatusCode = status;
        if (status == 503)
        {
            http.Response.Headers.RetryAfter = "10";
        }

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Type = $"https://httpstatuses.io/{status}",
                Detail = status switch
                {
                    422 => "One or more validation errors occurred.",
                    503 => "The service is temporarily unavailable. Please retry shortly.",
                    409 => "The resource was modified concurrently. Please retry.",
                    >= 500 => "An unexpected error occurred.",
                    _ => null,
                },
            },
        });
    }
}
