using System.Diagnostics;
using FluentValidation;
using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messenger.Application.Common;

/// <summary>1st: logs request name, outcome and duration. Never logs the request payload (bodies, passwords).</summary>
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull where TResponse : IResult
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var response = await next();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (response.IsSuccess)
        {
            logger.LogDebug("Handled {Request} in {ElapsedMs:0.0} ms", typeof(TRequest).Name, elapsed);
        }
        else
        {
            logger.LogInformation("Handled {Request} with {ErrorType}/{ErrorCode} in {ElapsedMs:0.0} ms",
                typeof(TRequest).Name, response.Error!.Type, response.Error.Code, elapsed);
        }

        return response;
    }
}

/// <summary>2nd: FluentValidation failures become a 422 validation error with per-field messages.</summary>
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull where TResponse : IResult
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(context, ct);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return await next();
        }

        var fields = failures
            .GroupBy(f => ToCamelCase(f.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray());
        return ResultFactory.Failure<TResponse>(new Error(ErrorType.Validation, "validation",
            "One or more validation errors occurred.", fields));
    }

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>3rd: authentication, system role and BOLA membership checks (NFR-01). Missing access is reported as 404
/// for conversation/message resources so existence is not leaked.</summary>
public sealed class AuthorizationBehavior<TRequest, TResponse>(ICurrentUser currentUser, IApplicationDbContext db)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull where TResponse : IResult
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is IAllowAnonymous)
        {
            return await next();
        }

        if (currentUser.UserId is not { } userId)
        {
            return ResultFactory.Failure<TResponse>(Error.Unauthorized());
        }

        if (request is IAdminRequest && !currentUser.IsAdmin)
        {
            return ResultFactory.Failure<TResponse>(Error.Forbidden("Administrator role required."));
        }

        if (request is IConversationMemberRequest conversationRequest)
        {
            var isMember = await db.Participants.AnyAsync(
                p => p.ConversationId == conversationRequest.ConversationId && p.UserId == userId && p.LeftAt == null, ct);
            if (!isMember)
            {
                return ResultFactory.Failure<TResponse>(Error.NotFound("Conversation"));
            }
        }

        if (request is IMessageAccessRequest messageRequest)
        {
            var canSee = await (
                from m in db.Messages
                join p in db.Participants on m.ConversationId equals p.ConversationId
                where m.Id == messageRequest.MessageId && p.UserId == userId && p.LeftAt == null
                      && m.Sequence >= p.VisibleFromSequence && m.Sequence > p.ClearedUpToSequence
                select m.Id).AnyAsync(ct);
            if (!canSee)
            {
                return ResultFactory.Failure<TResponse>(Error.NotFound("Message"));
            }
        }

        return await next();
    }
}

/// <summary>4th: commands run in one transaction under the EF execution strategy (retry on transient faults).</summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull where TResponse : IResult
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (!IsCommand())
        {
            return await next();
        }

        var commitOnFailure = request is ICommitOnFailure;
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync(_ => next(), r => r.IsSuccess || commitOnFailure, ct);
        }
        catch (UniqueConstraintException) when (request is IRetryOnUniqueViolation)
        {
            return await unitOfWork.ExecuteInTransactionAsync(_ => next(), r => r.IsSuccess || commitOnFailure, ct);
        }
    }

    private static bool IsCommand() => typeof(TRequest).GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>));
}
