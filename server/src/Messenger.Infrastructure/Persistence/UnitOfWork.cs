using MediatR;
using Messenger.Application.Abstractions;
using Messenger.Application.Common;
using Messenger.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Messenger.Infrastructure.Persistence;

/// <summary>Scoped buffer of events waiting for a successful commit (P-05).</summary>
public sealed class DomainEventBuffer : IDomainEventCollector
{
    private readonly List<IDomainEvent> _events = [];

    public void Add(IDomainEvent domainEvent) => _events.Add(domainEvent);

    public IReadOnlyList<IDomainEvent> TakeAll()
    {
        var events = _events.ToList();
        _events.Clear();
        return events;
    }

    public void Clear() => _events.Clear();
}

/// <summary>Moves events from tracked aggregates into the buffer whenever changes are saved.</summary>
public sealed class DomainEventInterceptor(DomainEventBuffer buffer) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is MessengerDbContext db)
        {
            foreach (var entity in db.TrackedEntitiesWithEvents().ToList())
            {
                foreach (var domainEvent in entity.DomainEvents)
                {
                    buffer.Add(domainEvent);
                }

                entity.ClearDomainEvents();
            }
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Publishes buffered events; handler failures are logged and never undo committed data.</summary>
public sealed class DomainEventDispatcher(DomainEventBuffer buffer, IPublisher publisher, ILogger<DomainEventDispatcher> logger)
{
    public async Task DispatchAsync()
    {
        for (var round = 0; round < 5; round++)
        {
            var events = buffer.TakeAll();
            if (events.Count == 0)
            {
                return;
            }

            foreach (var domainEvent in events)
            {
                var notification = (INotification)Activator.CreateInstance(
                    typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType()), domainEvent)!;
                try
                {
                    await publisher.Publish(notification, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handler for domain event {Event} failed", domainEvent.GetType().Name);
                }
            }
        }
    }
}

public sealed class UnitOfWork(MessengerDbContext db, DomainEventBuffer buffer, DomainEventDispatcher dispatcher) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await Translate(() => db.SaveChangesAsync(ct));
        if (db.Database.CurrentTransaction is null)
        {
            await dispatcher.DispatchAsync();
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> action, Func<T, bool> shouldCommit, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await action(ct);
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var result = await Translate(() => strategy.ExecuteAsync(async innerCt =>
        {
            buffer.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(innerCt);
            try
            {
                var value = await action(innerCt);
                if (shouldCommit(value))
                {
                    await db.SaveChangesAsync(innerCt);
                    await transaction.CommitAsync(innerCt);
                }
                else
                {
                    await transaction.RollbackAsync(innerCt);
                    db.ChangeTracker.Clear();
                    buffer.Clear();
                }

                return value;
            }
            catch
            {
                db.ChangeTracker.Clear();
                buffer.Clear();
                throw;
            }
        }, ct));

        await dispatcher.DispatchAsync();
        return result;
    }

    private static async Task<TResult> Translate<TResult>(Func<Task<TResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintException(pg.ConstraintName, ex);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new UniqueConstraintException(ex.ConstraintName, ex);
        }
    }
}
