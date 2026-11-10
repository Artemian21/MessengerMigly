using System.Collections.Concurrent;
using System.Reflection;
using MediatR;
using Messenger.Domain.Common;

namespace Messenger.Application.Common;

/// <summary>Commands change state and run inside the transaction behavior.</summary>
public interface ICommand<TResponse> : IRequest<TResponse> where TResponse : IResult;

/// <summary>Queries never change state; they use no-tracking projections.</summary>
public interface IQuery<TResponse> : IRequest<TResponse> where TResponse : IResult;

/// <summary>Anonymous requests (register, login, …). Everything else requires an authenticated user.</summary>
public interface IAllowAnonymous;

/// <summary>BOLA guard: the caller must be an active participant of the conversation.</summary>
public interface IConversationMemberRequest
{
    Guid ConversationId { get; }
}

/// <summary>BOLA guard: the caller must be an active participant of the message's conversation and able to see it.</summary>
public interface IMessageAccessRequest
{
    Guid MessageId { get; }
}

/// <summary>System role Admin is required (RBAC-01).</summary>
public interface IAdminRequest;

/// <summary>A concurrent unique-index violation re-runs the command once (idempotent send, get-or-create).</summary>
public interface IRetryOnUniqueViolation;

/// <summary>The transaction is committed even when the command returns a failure (failed-login counter, reuse detection).</summary>
public interface ICommitOnFailure;

/// <summary>MediatR wrapper so framework-free domain events can be published after commit.</summary>
public sealed record DomainEventNotification<TEvent>(TEvent Event) : INotification where TEvent : IDomainEvent;

internal static class ResultFactory
{
    private static readonly ConcurrentDictionary<Type, Func<Error, object>> Factories = new();

    public static TResponse Failure<TResponse>(Error error)
    {
        var factory = Factories.GetOrAdd(typeof(TResponse), static type =>
        {
            if (type == typeof(Result))
            {
                return e => Result.Failure(e);
            }

            var method = type.GetMethod(nameof(Result.Failure), BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                [typeof(Error)]) ?? throw new InvalidOperationException($"{type} has no Failure(Error) factory.");
            return e => method.Invoke(null, [e])!;
        });
        return (TResponse)factory(error);
    }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int? Next)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, int total, int offset, int limit) =>
        new(items, total, offset + items.Count < total ? offset + limit : null);
}

public static class Paging
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static int Limit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    public static int Offset(int? offset) => Math.Max(offset ?? 0, 0);
}
