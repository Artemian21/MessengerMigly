using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Messenger.Infrastructure.Persistence;

/// <summary>OPS-04: applies migrations at startup under a PostgreSQL advisory lock so parallel instances never race.</summary>
public sealed class DatabaseMigrator(MessengerDbContext db, ILogger<DatabaseMigrator> logger)
{
    private const long LockKey = 724_110_2026;

    public async Task MigrateAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({LockKey})", ct);
            try
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Applying {Count} database migration(s)", pending.Count);
                    await db.Database.MigrateAsync(ct);
                }
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({LockKey})", ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
